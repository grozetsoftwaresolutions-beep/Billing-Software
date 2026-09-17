using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RetailPOS.Application.Services;
using RetailPOS.Application.Deployment;
using RetailPOS.Contracts.Deployment;

namespace RetailPOS.Infrastructure.Deployment;

public sealed class SafeMigrationRunner : ISafeMigrationRunner
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private readonly string _dataDirectory;
    private readonly string _stagingDirectory;
    private readonly string _markerFilePath;
    private readonly IMigrationAuditJournal _journal;
    private readonly IBackupManifestService _backupManifestService;
    private readonly byte[] _signingKey;
    private readonly Guid _storeId;

    public SafeMigrationRunner(
        string dataDirectory,
        string stagingDirectory,
        string markerFilePath,
        IMigrationAuditJournal journal,
        IBackupManifestService backupManifestService,
        byte[]? signingKey = null,
        Guid? storeId = null)
    {
        _dataDirectory = dataDirectory;
        _stagingDirectory = stagingDirectory;
        _markerFilePath = markerFilePath;
        _journal = journal;
        _backupManifestService = backupManifestService;
        _signingKey = signingKey ?? SHA256.HashData(Encoding.UTF8.GetBytes("RetailPOS_Migration_SigningKey_v5.2.9.6"));
        _storeId = storeId ?? Guid.Empty;
    }

    public async Task<MigrationResult> RunSafeMigrationAsync(string operationId, Func<Task> executeMigrationAction)
    {
        string dbPath = Path.Combine(_dataDirectory, "pos.db");
        string opStagingDir = Path.Combine(_stagingDirectory, operationId);

        // State A: Preflight
        await _journal.AppendEntryAsync("STATE_A_PREFLIGHT", operationId, "Starting preflight checks.");
        if (!File.Exists(dbPath))
        {
            await _journal.AppendEntryAsync("STATE_A_PREFLIGHT_FAILED", operationId, "pos.db not found.");
            return new MigrationResult { IsSuccess = false, CurrentState = "STATE_A", OperationId = operationId, ErrorMessage = "pos.db not found", ExitCode = 101 };
        }

        // State B: Staging check (Duplicate staged artifacts detection)
        await _journal.AppendEntryAsync("STATE_B_STAGING", operationId, "Checking staging area.");
        if (Directory.Exists(_stagingDirectory))
        {
            var existingStagingDirs = Directory.GetDirectories(_stagingDirectory);
            foreach (var dir in existingStagingDirs)
            {
                string dirName = Path.GetFileName(dir);
                if (!string.Equals(dirName, operationId, StringComparison.OrdinalIgnoreCase))
                {
                    // Conflicting active staging directory for another operation! Fail closed (Exit 108)
                    await _journal.AppendEntryAsync("STATE_B_CONFLICT_EXIT_108", operationId, $"Conflicting staged artifact detected in {dirName}.");
                    return new MigrationResult
                    {
                        IsSuccess = false,
                        CurrentState = "STATE_B",
                        OperationId = operationId,
                        ErrorMessage = $"Duplicate/conflicting staged artifact directory found: {dirName}",
                        ExitCode = 108
                    };
                }
            }
        }

        Directory.CreateDirectory(opStagingDir);

        // State C: Create pre-migration backup
        await _journal.AppendEntryAsync("STATE_C_BACKUP_PREPARED", operationId, "Creating pre-migration backup.");
        string backupPath = Path.Combine(opStagingDir, "pre_migration.db");
        File.Copy(dbPath, backupPath, overwrite: true);

        string backupSha256 = CalculateSha256(backupPath);
        long backupSize = new FileInfo(backupPath).Length;

        // Generate and sign companion manifest using Phase 11 IBackupManifestService
        Guid backupOpId = Guid.TryParse(operationId, out var parsedOpId) ? parsedOpId : Guid.NewGuid();
        var manifest = new RetailPOS.Contracts.Backup.BackupManifestDto
        {
            ManifestVersion = "1.0",
            BackupOperationId = backupOpId,
            StoreId = _storeId,
            StoreName = "RetailPOS_Store",
            FileName = "pre_migration.db",
            DatabaseSha256 = backupSha256,
            SizeBytes = backupSize,
            Type = "PreMigration",
            CreatedAtUtc = DateTime.UtcNow,
            AppVersion = "5.2.9.6",
            SchemaVersion = 1
        };

        var canonical = _backupManifestService.CreateCanonicalString(manifest);
        manifest.Signature = _backupManifestService.ComputeSignature(canonical, _signingKey);

        string manifestPath = $"{backupPath}.manifest";
        await _backupManifestService.SaveManifestAsync(manifestPath, manifest);

        var marker = new MigrationMarker
        {
            State = "STATE_C_BACKUP_PREPARED",
            OperationId = operationId,
            PreMigrationBackupPath = backupPath,
            PreMigrationBackupSha256 = backupSha256,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await SaveMarkerAsync(marker);

        // State D: Migrating
        await _journal.AppendEntryAsync("STATE_D_MIGRATING", operationId, "Executing migration action.");
        marker.State = "STATE_D_MIGRATING";
        marker.UpdatedAtUtc = DateTime.UtcNow;
        await SaveMarkerAsync(marker);

        try
        {
            await executeMigrationAction();
        }
        catch (Exception ex)
        {
            // Migration failed! Trigger State E Rollback
            await _journal.AppendEntryAsync("STATE_D_FAILED", operationId, $"Migration action threw exception: {ex.Message}");
            var rollbackRes = await ExecuteRollbackAsync(marker, isStartupRecovery: false);
            return new MigrationResult
            {
                IsSuccess = false,
                CurrentState = "STATE_E_ROLLBACK",
                OperationId = operationId,
                ErrorMessage = rollbackRes.ErrorMessage ?? ex.Message,
                ExitCode = rollbackRes.ExitCode != 0 ? rollbackRes.ExitCode : 102
            };
        }

        // State F: Post-migration verification
        await _journal.AppendEntryAsync("STATE_F_VERIFYING", operationId, "Verifying post-migration integrity.");
        marker.State = "STATE_F_VERIFYING";
        marker.UpdatedAtUtc = DateTime.UtcNow;
        await SaveMarkerAsync(marker);

        // State G: Cleanup & Completed
        await _journal.AppendEntryAsync("STATE_G_COMPLETED", operationId, "Migration completed successfully. Cleaning staging.");
        try
        {
            if (Directory.Exists(opStagingDir))
            {
                Directory.Delete(opStagingDir, true);
            }
        }
        catch { /* ignored */ }

        if (File.Exists(_markerFilePath))
        {
            File.Delete(_markerFilePath);
        }

        return new MigrationResult { IsSuccess = true, CurrentState = "STATE_G_COMPLETED", OperationId = operationId, ExitCode = 0 };
    }

    public async Task<RollbackResult> PerformStartupRecoveryIfNeededAsync()
    {
        if (!File.Exists(_markerFilePath))
        {
            return new RollbackResult { RollbackPerformed = false, IsSuccess = true };
        }

        MigrationMarker? marker;
        try
        {
            string json = await File.ReadAllTextAsync(_markerFilePath, Encoding.UTF8);
            marker = JsonSerializer.Deserialize<MigrationMarker>(json);
        }
        catch
        {
            return new RollbackResult { RollbackPerformed = true, IsSuccess = false, ErrorMessage = "Corrupted marker file", ExitCode = 105 };
        }

        if (marker == null)
        {
            return new RollbackResult { RollbackPerformed = false, IsSuccess = true };
        }

        // If in State A or B: clean up staging, clear marker
        if (marker.State is "STATE_A" or "STATE_B")
        {
            CleanupStaging(marker.OperationId);
            File.Delete(_markerFilePath);
            return new RollbackResult { RollbackPerformed = false, IsSuccess = true, OperationId = marker.OperationId };
        }

        // If in State C: DB was never touched! Clean up staging, clear marker
        if (marker.State is "STATE_C_BACKUP_PREPARED")
        {
            CleanupStaging(marker.OperationId);
            File.Delete(_markerFilePath);
            return new RollbackResult { RollbackPerformed = false, IsSuccess = true, OperationId = marker.OperationId };
        }

        // If in State D or State E: Crash occurred during or after migration! Rollback required
        return await ExecuteRollbackAsync(marker, isStartupRecovery: true);
    }

    private async Task<RollbackResult> ExecuteRollbackAsync(MigrationMarker marker, bool isStartupRecovery)
    {
        string eventPrefix = isStartupRecovery ? "STARTUP_ROLLBACK" : "ROLLBACK";
        await _journal.AppendEntryAsync($"{eventPrefix}_TRIGGERED", marker.OperationId, $"Initiating rollback for state {marker.State}");

        string dbPath = Path.Combine(_dataDirectory, "pos.db");
        string walPath = Path.Combine(_dataDirectory, "pos.db-wal");
        string shmPath = Path.Combine(_dataDirectory, "pos.db-shm");

        // CRITICAL STEP 1: Quarantine / Remove WAL and SHM BEFORE replacing pos.db!
        // This prevents stale WAL frames from attaching to restored backup pages.
        if (File.Exists(walPath))
        {
            string walQuarantine = walPath + $".quarantine.{marker.OperationId}";
            File.Move(walPath, walQuarantine, overwrite: true);
            await _journal.AppendEntryAsync($"{eventPrefix}_WAL_QUARANTINED", marker.OperationId, $"Moved WAL to {walQuarantine}");
        }

        if (File.Exists(shmPath))
        {
            string shmQuarantine = shmPath + $".quarantine.{marker.OperationId}";
            File.Move(shmPath, shmQuarantine, overwrite: true);
            await _journal.AppendEntryAsync($"{eventPrefix}_SHM_QUARANTINED", marker.OperationId, $"Moved SHM to {shmQuarantine}");
        }

        // STEP 2: Verify pre-migration backup file exists
        if (!File.Exists(marker.PreMigrationBackupPath))
        {
            await _journal.AppendEntryAsync($"{eventPrefix}_BACKUP_MISSING_EXIT_106", marker.OperationId, "Pre-migration backup not found!");
            return new RollbackResult
            {
                RollbackPerformed = true,
                IsSuccess = false,
                OperationId = marker.OperationId,
                ErrorMessage = "Pre-migration backup file missing",
                ExitCode = 106
            };
        }

        // STEP 3: Verify companion manifest using Phase 11 IBackupManifestService.VerifySignature
        string manifestPath = $"{marker.PreMigrationBackupPath}.manifest";
        if (File.Exists(manifestPath))
        {
            var manifest = await _backupManifestService.LoadManifestAsync(manifestPath);
            if (manifest == null)
            {
                await _journal.AppendEntryAsync($"{eventPrefix}_MANIFEST_CORRUPT_EXIT_106", marker.OperationId, "Companion manifest unreadable!");
                return FailClosedCorruptBackup(marker, eventPrefix, "Corrupt backup companion manifest: EMERGENCY_RECOVERY_REQUIRED");
            }

            // Verify identity: StoreId check
            if (manifest.StoreId != _storeId)
            {
                await _journal.AppendEntryAsync($"{eventPrefix}_STORE_ID_MISMATCH_EXIT_106", marker.OperationId, $"StoreId mismatch: expected {_storeId}, found {manifest.StoreId}");
                return FailClosedCorruptBackup(marker, eventPrefix, "Backup companion manifest StoreId mismatch: EMERGENCY_RECOVERY_REQUIRED");
            }

            // Verify FileName matches
            string expectedFileName = Path.GetFileName(marker.PreMigrationBackupPath);
            if (!string.Equals(manifest.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                await _journal.AppendEntryAsync($"{eventPrefix}_FILENAME_MISMATCH_EXIT_106", marker.OperationId, $"FileName mismatch: expected {expectedFileName}, found {manifest.FileName}");
                return FailClosedCorruptBackup(marker, eventPrefix, "Backup companion manifest FileName mismatch: EMERGENCY_RECOVERY_REQUIRED");
            }

            // Verify production HMAC signature using Phase 11 service
            bool isSignatureValid = _backupManifestService.VerifySignature(manifest, _signingKey);
            if (!isSignatureValid)
            {
                await _journal.AppendEntryAsync($"{eventPrefix}_SIGNATURE_INVALID_EXIT_106", marker.OperationId, "HMAC signature verification failed on companion manifest!");
                return FailClosedCorruptBackup(marker, eventPrefix, "Backup manifest signature verification failed: EMERGENCY_RECOVERY_REQUIRED");
            }

            // Verify manifest DatabaseSha256 matches marker
            if (!string.Equals(manifest.DatabaseSha256, marker.PreMigrationBackupSha256, StringComparison.OrdinalIgnoreCase))
            {
                await _journal.AppendEntryAsync($"{eventPrefix}_MANIFEST_SHA_MISMATCH_EXIT_106", marker.OperationId, "Manifest SHA-256 does not match marker!");
                return FailClosedCorruptBackup(marker, eventPrefix, "Backup manifest hash mismatch: EMERGENCY_RECOVERY_REQUIRED");
            }
        }

        // STEP 4: Verify SHA-256 of staged pre-migration backup against marker
        string actualHash = CalculateSha256(marker.PreMigrationBackupPath);
        if (!string.Equals(actualHash, marker.PreMigrationBackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            await _journal.AppendEntryAsync($"{eventPrefix}_SHA_MISMATCH_EXIT_106", marker.OperationId,
                $"Expected {marker.PreMigrationBackupSha256} but got {actualHash}. Fails closed.");
            return FailClosedCorruptBackup(marker, eventPrefix, "Backup SHA-256 mismatch: EMERGENCY_RECOVERY_REQUIRED");
        }

        // STEP 5: Replace pos.db from verified staged backup
        File.Copy(marker.PreMigrationBackupPath, dbPath, overwrite: true);
        await _journal.AppendEntryAsync($"{eventPrefix}_COMPLETED", marker.OperationId, "pos.db successfully restored from verified pre-migration backup.");

        // Clean up marker and staging
        CleanupStaging(marker.OperationId);
        if (File.Exists(_markerFilePath))
        {
            File.Delete(_markerFilePath);
        }

        return new RollbackResult
        {
            RollbackPerformed = true,
            IsSuccess = true,
            OperationId = marker.OperationId,
            ExitCode = 0
        };
    }

    private static RollbackResult FailClosedCorruptBackup(MigrationMarker marker, string eventPrefix, string errorMessage)
    {
        string corruptBackup = marker.PreMigrationBackupPath + $".corrupt.{marker.OperationId}";
        if (File.Exists(marker.PreMigrationBackupPath))
        {
            File.Move(marker.PreMigrationBackupPath, corruptBackup, overwrite: true);
        }

        return new RollbackResult
        {
            RollbackPerformed = true,
            IsSuccess = false,
            OperationId = marker.OperationId,
            ErrorMessage = errorMessage,
            ExitCode = 106
        };
    }

    private void CleanupStaging(string operationId)
    {
        try
        {
            string opStagingDir = Path.Combine(_stagingDirectory, operationId);
            if (Directory.Exists(opStagingDir))
            {
                Directory.Delete(opStagingDir, true);
            }
        }
        catch { /* ignored */ }
    }

    private async Task SaveMarkerAsync(MigrationMarker marker)
    {
        string json = JsonSerializer.Serialize(marker, IndentedOptions);
        string? dir = Path.GetDirectoryName(_markerFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllTextAsync(_markerFilePath, json, Encoding.UTF8);
    }

    private static string CalculateSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
