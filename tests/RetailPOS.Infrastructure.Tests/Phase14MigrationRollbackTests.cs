using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Application.Services;
using RetailPOS.Application.Deployment;
using RetailPOS.Contracts.Deployment;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Deployment;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class Phase14MigrationRollbackTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dataDir;
    private readonly string _stagingDir;
    private readonly string _markerPath;
    private readonly string _journalPath;
    private readonly byte[] _hmacKey;
    private readonly FileMigrationAuditJournal _journal;
    private readonly IBackupManifestService _backupManifestService;

    public Phase14MigrationRollbackTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"migration_test_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_testDir, "Data");
        _stagingDir = Path.Combine(_testDir, "Staging");
        _markerPath = Path.Combine(_testDir, "migration_state.json");
        _journalPath = Path.Combine(_testDir, "migration_audit.journal");

        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_stagingDir);

        _hmacKey = RandomNumberGenerator.GetBytes(32);
        _journal = new FileMigrationAuditJournal(_journalPath, _hmacKey);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IBackupManifestService, BackupManifestService>();
        var provider = services.BuildServiceProvider();
        _backupManifestService = provider.GetRequiredService<IBackupManifestService>();

        // Create an initial pos.db
        File.WriteAllText(Path.Combine(_dataDir, "pos.db"), "INITIAL_POS_DB_CONTENT_V1");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { /* ignored */ }
    }

    private SafeMigrationRunner CreateRunner() =>
        new SafeMigrationRunner(_dataDir, _stagingDir, _markerPath, _journal, _backupManifestService);

    [Fact]
    public async Task TEST_23_Migration_CleanRun_CompletesStatesAThroughG()
    {
        var runner = CreateRunner();
        string opId = Guid.NewGuid().ToString("N");

        var result = await runner.RunSafeMigrationAsync(opId, async () =>
        {
            // Simulate migration modifying pos.db
            await File.WriteAllTextAsync(Path.Combine(_dataDir, "pos.db"), "MIGRATED_POS_DB_CONTENT_V2");
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("STATE_G_COMPLETED", result.CurrentState);
        Assert.Equal(0, result.ExitCode);

        // Verify pos.db has migrated content
        string currentContent = await File.ReadAllTextAsync(Path.Combine(_dataDir, "pos.db"));
        Assert.Equal("MIGRATED_POS_DB_CONTENT_V2", currentContent);

        // Verify staging directory cleaned
        Assert.False(Directory.Exists(Path.Combine(_stagingDir, opId)));
        Assert.False(File.Exists(_markerPath));

        // Verify journal chain
        bool valid = await _journal.VerifyChainIntegrityAsync();
        Assert.True(valid);
    }

    [Fact]
    public async Task TEST_24_Migration_CrashStateA_NoArtifactsLeft()
    {
        // Delete pos.db to cause State A failure
        File.Delete(Path.Combine(_dataDir, "pos.db"));

        var runner = CreateRunner();
        string opId = Guid.NewGuid().ToString("N");

        var result = await runner.RunSafeMigrationAsync(opId, () => Task.CompletedTask);

        Assert.False(result.IsSuccess);
        Assert.Equal("STATE_A", result.CurrentState);
        Assert.Equal(101, result.ExitCode);

        // No staging directory or marker left
        Assert.False(Directory.Exists(Path.Combine(_stagingDir, opId)));
        Assert.False(File.Exists(_markerPath));
    }

    [Fact]
    public async Task TEST_25_Migration_CrashStateB_StagedCleanedOnRestart()
    {
        string opId = Guid.NewGuid().ToString("N");
        string opStaging = Path.Combine(_stagingDir, opId);
        Directory.CreateDirectory(opStaging);
        await File.WriteAllTextAsync(Path.Combine(opStaging, "temp.bin"), "TEST");

        var marker = new MigrationMarker
        {
            State = "STATE_B",
            OperationId = opId,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(_markerPath, JsonSerializer.Serialize(marker));

        var runner = CreateRunner();
        var recoveryResult = await runner.PerformStartupRecoveryIfNeededAsync();

        Assert.True(recoveryResult.IsSuccess);
        Assert.False(recoveryResult.RollbackPerformed);
        Assert.False(Directory.Exists(opStaging));
        Assert.False(File.Exists(_markerPath));
    }

    [Fact]
    public async Task TEST_26_Migration_CrashStateC_NoPendingRollback_CleanStart()
    {
        string opId = Guid.NewGuid().ToString("N");
        string opStaging = Path.Combine(_stagingDir, opId);
        Directory.CreateDirectory(opStaging);
        string backupFile = Path.Combine(opStaging, "pre_migration.db");
        await File.WriteAllTextAsync(backupFile, "INITIAL_POS_DB_CONTENT_V1");

        var marker = new MigrationMarker
        {
            State = "STATE_C_BACKUP_PREPARED",
            OperationId = opId,
            PreMigrationBackupPath = backupFile,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(_markerPath, JsonSerializer.Serialize(marker));

        var runner = CreateRunner();
        var recoveryResult = await runner.PerformStartupRecoveryIfNeededAsync();

        Assert.True(recoveryResult.IsSuccess);
        Assert.False(recoveryResult.RollbackPerformed);
        Assert.False(Directory.Exists(opStaging));
        Assert.False(File.Exists(_markerPath));
        Assert.Equal("INITIAL_POS_DB_CONTENT_V1", await File.ReadAllTextAsync(Path.Combine(_dataDir, "pos.db")));
    }

    [Fact]
    public async Task TEST_27_Migration_CrashStateD_AutomaticRollbackOnStartup()
    {
        string opId = Guid.NewGuid().ToString("N");
        string opStaging = Path.Combine(_stagingDir, opId);
        Directory.CreateDirectory(opStaging);
        string backupFile = Path.Combine(opStaging, "pre_migration.db");
        await File.WriteAllTextAsync(backupFile, "INITIAL_POS_DB_CONTENT_V1");

        string shaHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("INITIAL_POS_DB_CONTENT_V1")));

        var marker = new MigrationMarker
        {
            State = "STATE_D_MIGRATING",
            OperationId = opId,
            PreMigrationBackupPath = backupFile,
            PreMigrationBackupSha256 = shaHex,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(_markerPath, JsonSerializer.Serialize(marker));

        // Simulate half-applied migration corruption in pos.db
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "pos.db"), "CORRUPTED_IN_FLIGHT_DATA");

        var runner = CreateRunner();
        var recoveryResult = await runner.PerformStartupRecoveryIfNeededAsync();

        Assert.True(recoveryResult.IsSuccess);
        Assert.True(recoveryResult.RollbackPerformed);
        Assert.Equal(0, recoveryResult.ExitCode);

        // pos.db is restored to pristine V1
        string restored = await File.ReadAllTextAsync(Path.Combine(_dataDir, "pos.db"));
        Assert.Equal("INITIAL_POS_DB_CONTENT_V1", restored);

        // Staging cleaned
        Assert.False(Directory.Exists(opStaging));
        Assert.False(File.Exists(_markerPath));
    }

    [Fact]
    public async Task TEST_28_Migration_CrashStateE_AutomaticRollback_QuarantinesWalShmBeforePosDb()
    {
        string opId = Guid.NewGuid().ToString("N");
        string opStaging = Path.Combine(_stagingDir, opId);
        Directory.CreateDirectory(opStaging);
        string backupFile = Path.Combine(opStaging, "pre_migration.db");
        await File.WriteAllTextAsync(backupFile, "INITIAL_POS_DB_CONTENT_V1");

        string shaHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("INITIAL_POS_DB_CONTENT_V1")));

        var marker = new MigrationMarker
        {
            State = "STATE_E_ROLLBACK",
            OperationId = opId,
            PreMigrationBackupPath = backupFile,
            PreMigrationBackupSha256 = shaHex,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(_markerPath, JsonSerializer.Serialize(marker));

        // Simulate active WAL and SHM files
        string walPath = Path.Combine(_dataDir, "pos.db-wal");
        string shmPath = Path.Combine(_dataDir, "pos.db-shm");
        await File.WriteAllTextAsync(walPath, "STALE_WAL_FRAMES");
        await File.WriteAllTextAsync(shmPath, "STALE_SHM_INDEX");

        var runner = CreateRunner();
        var recoveryResult = await runner.PerformStartupRecoveryIfNeededAsync();

        Assert.True(recoveryResult.IsSuccess);
        Assert.True(recoveryResult.RollbackPerformed);

        // Primary WAL and SHM must be gone!
        Assert.False(File.Exists(walPath));
        Assert.False(File.Exists(shmPath));

        // Quarantined versions must exist
        Assert.True(File.Exists(walPath + $".quarantine.{opId}"));
        Assert.True(File.Exists(shmPath + $".quarantine.{opId}"));

        // Content restored
        Assert.Equal("INITIAL_POS_DB_CONTENT_V1", await File.ReadAllTextAsync(Path.Combine(_dataDir, "pos.db")));
    }

    [Fact]
    public async Task TEST_29_Migration_CrashStateE_ShaMismatch_FailsClosed_Exit106()
    {
        string opId = Guid.NewGuid().ToString("N");
        string opStaging = Path.Combine(_stagingDir, opId);
        Directory.CreateDirectory(opStaging);
        string backupFile = Path.Combine(opStaging, "pre_migration.db");
        await File.WriteAllTextAsync(backupFile, "TAMPERED_BACKUP_CONTENT");

        var marker = new MigrationMarker
        {
            State = "STATE_E_ROLLBACK",
            OperationId = opId,
            PreMigrationBackupPath = backupFile,
            PreMigrationBackupSha256 = "EXPECTED_VALID_HASH_WHICH_DOES_NOT_MATCH",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(_markerPath, JsonSerializer.Serialize(marker));

        // Current pos.db
        string currentDbContent = "CURRENT_IN_FLIGHT_DB";
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "pos.db"), currentDbContent);

        var runner = CreateRunner();
        var recoveryResult = await runner.PerformStartupRecoveryIfNeededAsync();

        Assert.False(recoveryResult.IsSuccess);
        Assert.Equal(106, recoveryResult.ExitCode);
        Assert.Contains("EMERGENCY_RECOVERY_REQUIRED", recoveryResult.ErrorMessage);

        // pos.db was NOT overwritten with bad backup!
        Assert.Equal(currentDbContent, await File.ReadAllTextAsync(Path.Combine(_dataDir, "pos.db")));

        // Tampered backup was quarantined to .corrupt.<OpId>
        Assert.True(File.Exists(backupFile + $".corrupt.{opId}"));
    }

    [Fact]
    public async Task TEST_30_Migration_DuplicateStagedArtifacts_FailClosed_Exit108()
    {
        string conflictingOpId = "conflicting_op_123";
        Directory.CreateDirectory(Path.Combine(_stagingDir, conflictingOpId));

        var runner = CreateRunner();
        string currentOpId = "current_op_456";

        var result = await runner.RunSafeMigrationAsync(currentOpId, () => Task.CompletedTask);

        Assert.False(result.IsSuccess);
        Assert.Equal("STATE_B", result.CurrentState);
        Assert.Equal(108, result.ExitCode);
        Assert.Contains("Duplicate/conflicting staged artifact", result.ErrorMessage);
    }

    [Fact]
    public async Task TEST_31_Migration_ExternalHmacAuditJournal_VerifiedAcrossRollback()
    {
        var runner = CreateRunner();
        string opId = Guid.NewGuid().ToString("N");

        // Run migration that throws exception
        var result = await runner.RunSafeMigrationAsync(opId, () => throw new InvalidOperationException("DB migration syntax error"));
        Assert.False(result.IsSuccess);
        Assert.Equal("STATE_E_ROLLBACK", result.CurrentState);

        // Verify HMAC chain integrity across all entries
        bool valid = await _journal.VerifyChainIntegrityAsync();
        Assert.True(valid);

        var entries = await _journal.ReadEntriesAsync();
        Assert.True(entries.Count >= 4);
        Assert.Equal("STATE_A_PREFLIGHT", entries[0].EventType);
        Assert.Equal("STATE_B_STAGING", entries[1].EventType);
        Assert.Equal("STATE_C_BACKUP_PREPARED", entries[2].EventType);
        Assert.Equal("STATE_D_MIGRATING", entries[3].EventType);
    }

    [Fact]
    public async Task TEST_32_Migration_ProductionDIRepositoryReuse_InheritsPhase11()
    {
        // 1. Production DI verification: proves real DI container resolution
        Assert.NotNull(_backupManifestService);
        Assert.IsType<BackupManifestService>(_backupManifestService);

        string opId = Guid.NewGuid().ToString("N");
        var runner = CreateRunner();

        // 2. Legitimate migration run: verifies State C generates companion manifest signed with Phase 11 service
        var migrationResult = await runner.RunSafeMigrationAsync(opId, async () =>
        {
            // Verify companion manifest created during State C
            string manifestPath = Path.Combine(_stagingDir, opId, "pre_migration.db.manifest");
            Assert.True(File.Exists(manifestPath), "Companion manifest must be created in staging.");

            var manifest = await _backupManifestService.LoadManifestAsync(manifestPath);
            Assert.NotNull(manifest);
            Assert.Equal("pre_migration.db", manifest.FileName);
            Assert.True(manifest.SchemaVersion >= 1);
            Assert.NotEmpty(manifest.Signature);

            // Verify signature using Phase 11 IBackupManifestService.VerifySignature
            byte[] defaultKey = SHA256.HashData(Encoding.UTF8.GetBytes("RetailPOS_Migration_SigningKey_v5.2.9.6"));
            bool isSigValid = _backupManifestService.VerifySignature(manifest, defaultKey);
            Assert.True(isSigValid, "Companion manifest signature must be valid under Phase 11 verifier.");

            // Modify pos.db to simulate successful migration
            await File.WriteAllTextAsync(Path.Combine(_dataDir, "pos.db"), "MIGRATED_CONTENT");
        });

        Assert.True(migrationResult.IsSuccess);
        Assert.Equal("STATE_G_COMPLETED", migrationResult.CurrentState);

        // 3. Tampered manifest case: proves VerifySignature fails and SafeMigrationRunner refuses backup (Exit 106)
        string crashOpId = Guid.NewGuid().ToString("N");
        string crashStaging = Path.Combine(_stagingDir, crashOpId);
        Directory.CreateDirectory(crashStaging);
        string backupFile = Path.Combine(crashStaging, "pre_migration.db");
        await File.WriteAllTextAsync(backupFile, "LEGIT_BACKUP_CONTENT");
        string shaHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("LEGIT_BACKUP_CONTENT")));

        // Create companion manifest but with TAMPERED signature
        var tamperedManifest = new RetailPOS.Contracts.Backup.BackupManifestDto
        {
            ManifestVersion = "1.0",
            BackupOperationId = Guid.NewGuid(),
            StoreId = Guid.Empty,
            StoreName = "RetailPOS_Store",
            FileName = "pre_migration.db",
            DatabaseSha256 = shaHex,
            SizeBytes = new FileInfo(backupFile).Length,
            Type = "PreMigration",
            CreatedAtUtc = DateTime.UtcNow,
            AppVersion = "5.2.9.6",
            SchemaVersion = 1,
            Signature = "DEADBEEFCAFE00112233445566778899AABBCCDDEEFF00112233445566778899" // Forged signature
        };
        await _backupManifestService.SaveManifestAsync($"{backupFile}.manifest", tamperedManifest);

        var marker = new MigrationMarker
        {
            State = "STATE_E_ROLLBACK",
            OperationId = crashOpId,
            PreMigrationBackupPath = backupFile,
            PreMigrationBackupSha256 = shaHex,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(_markerPath, JsonSerializer.Serialize(marker));

        var recoveryResult = await runner.PerformStartupRecoveryIfNeededAsync();

        // Must refuse corrupted/forged manifest and fail closed with Exit 106
        Assert.False(recoveryResult.IsSuccess);
        Assert.Equal(106, recoveryResult.ExitCode);
        Assert.Contains("EMERGENCY_RECOVERY_REQUIRED", recoveryResult.ErrorMessage);
        Assert.True(File.Exists(backupFile + $".corrupt.{crashOpId}"));

        // 4. Identity mismatch case: valid signature on different StoreId remains rejected
        string mismatchOpId = Guid.NewGuid().ToString("N");
        string mismatchStaging = Path.Combine(_stagingDir, mismatchOpId);
        Directory.CreateDirectory(mismatchStaging);
        string mismatchBackup = Path.Combine(mismatchStaging, "pre_migration.db");
        await File.WriteAllTextAsync(mismatchBackup, "MISMATCH_STORE_CONTENT");
        string mismatchSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("MISMATCH_STORE_CONTENT")));

        byte[] defaultKey2 = SHA256.HashData(Encoding.UTF8.GetBytes("RetailPOS_Migration_SigningKey_v5.2.9.6"));
        var mismatchManifest = new RetailPOS.Contracts.Backup.BackupManifestDto
        {
            ManifestVersion = "1.0",
            BackupOperationId = Guid.NewGuid(),
            StoreId = Guid.NewGuid(), // DIFFERENT STORE ID!
            StoreName = "Rogue_Store",
            FileName = "pre_migration.db",
            DatabaseSha256 = mismatchSha,
            SizeBytes = new FileInfo(mismatchBackup).Length,
            Type = "PreMigration",
            CreatedAtUtc = DateTime.UtcNow,
            AppVersion = "5.2.9.6",
            SchemaVersion = 1
        };
        var canonical = _backupManifestService.CreateCanonicalString(mismatchManifest);
        mismatchManifest.Signature = _backupManifestService.ComputeSignature(canonical, defaultKey2);
        await _backupManifestService.SaveManifestAsync($"{mismatchBackup}.manifest", mismatchManifest);

        var mismatchMarker = new MigrationMarker
        {
            State = "STATE_E_ROLLBACK",
            OperationId = mismatchOpId,
            PreMigrationBackupPath = mismatchBackup,
            PreMigrationBackupSha256 = mismatchSha,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(_markerPath, JsonSerializer.Serialize(mismatchMarker));

        var mismatchResult = await runner.PerformStartupRecoveryIfNeededAsync();
        Assert.False(mismatchResult.IsSuccess);
        Assert.Equal(106, mismatchResult.ExitCode);
        Assert.Contains("StoreId mismatch", mismatchResult.ErrorMessage);
    }
}
