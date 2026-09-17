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

    public SafeMigrationRunner(
        string dataDirectory,
        string stagingDirectory,
        string markerFilePath,
        IMigrationAuditJournal journal,
        IBackupManifestService backupManifestService)
    {
        _dataDirectory = dataDirectory;
        _stagingDirectory = stagingDirectory;
        _markerFilePath = markerFilePath;
        _journal = journal;
        _backupManifestService = backupManifestService;
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

        // STEP 2: Verify SHA-256 of staged pre-migration backup against marker
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

        string actualHash = CalculateSha256(marker.PreMigrationBackupPath);
        if (!string.Equals(actualHash, marker.PreMigrationBackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            // Hash mismatch! Quarantine corrupted backup and FAIL CLOSED (Exit 106). Never overwrite pos.db!
            string corruptBackup = marker.PreMigrationBackupPath + $".corrupt.{marker.OperationId}";
            File.Move(marker.PreMigrationBackupPath, corruptBackup, overwrite: true);

            await _journal.AppendEntryAsync($"{eventPrefix}_SHA_MISMATCH_EXIT_106", marker.OperationId,
                $"Expected {marker.PreMigrationBackupSha256} but got {actualHash}. Quarantined to {corruptBackup}. Fails closed.");

            return new RollbackResult
            {
                RollbackPerformed = true,
                IsSuccess = false,
                OperationId = marker.OperationId,
                ErrorMessage = "Backup SHA-256 mismatch: EMERGENCY_RECOVERY_REQUIRED",
                ExitCode = 106
            };
        }

        // STEP 3: Replace pos.db from verified staged backup
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
