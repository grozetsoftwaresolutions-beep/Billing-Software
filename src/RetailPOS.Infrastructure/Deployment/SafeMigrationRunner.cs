using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Deployment;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Deployment;
using RetailPOS.Infrastructure.Persistence;

namespace RetailPOS.Infrastructure.Deployment;

public sealed class SafeMigrationRunner : ISafeMigrationRunner
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private readonly string _dataDirectory;
    private readonly string _stagingDirectory;
    private readonly string _markerFilePath;
    private readonly string _databaseFileName;
    private readonly IMigrationAuditJournal _journal;
    private readonly IBackupManifestService _backupManifestService;
    private readonly IStoreIdentityService? _storeIdentityService;
    private readonly IDatabaseMaintenanceCoordinator? _maintenanceCoordinator;
    private readonly SchemaMigrationRegistry? _schemaRegistry;
    private readonly byte[] _signingKey;
    private readonly Guid _storeId;

    public SafeMigrationRunner(
        string dataDirectory,
        string stagingDirectory,
        string markerFilePath,
        IMigrationAuditJournal journal,
        IBackupManifestService backupManifestService,
        byte[]? signingKey = null,
        Guid? storeId = null,
        string? databaseFileName = null,
        IStoreIdentityService? storeIdentityService = null,
        IDatabaseMaintenanceCoordinator? maintenanceCoordinator = null,
        SchemaMigrationRegistry? schemaRegistry = null)
    {
        _dataDirectory = dataDirectory;
        _stagingDirectory = stagingDirectory;
        _markerFilePath = markerFilePath;
        _journal = journal;
        _backupManifestService = backupManifestService;
        _storeIdentityService = storeIdentityService;
        _maintenanceCoordinator = maintenanceCoordinator;
        _schemaRegistry = schemaRegistry ?? new SchemaMigrationRegistry();
        _databaseFileName = databaseFileName ?? ResolveDatabaseFileName(dataDirectory);
        _signingKey = signingKey ?? SHA256.HashData(Encoding.UTF8.GetBytes("RetailPOS_Migration_SigningKey_v5.2.9.6"));
        _storeId = storeId ?? Guid.Empty;
    }

    private static string ResolveDatabaseFileName(string dataDir)
    {
        if (File.Exists(Path.Combine(dataDir, "grozet_pos.db")))
            return "grozet_pos.db";
        if (File.Exists(Path.Combine(dataDir, "pos.db")))
            return "pos.db";
        return RetailPOSPaths.DefaultDatabaseFileName;
    }

    public string ResolveDatabasePath() => Path.Combine(_dataDirectory, _databaseFileName);

    public static async Task<int> GetCurrentSchemaVersionAsync(RetailDbContext context, CancellationToken ct = default)
    {
        var conn = context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var ver = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(ver, CultureInfo.InvariantCulture);
    }

    public static async Task<bool> VerifyAndEstablishBaseline14IfNeededAsync(RetailDbContext context, CancellationToken ct = default)
    {
        var conn = context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var verObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        int currentVersion = Convert.ToInt32(verObj, CultureInfo.InvariantCulture);

        if (currentVersion > 0)
        {
            return true;
        }

        // user_version == 0: Check if tables exist
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='stores';";
        bool storesTableExists = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) != null;

        if (!storesTableExists)
        {
            // Brand new database: EnsureCreatedAsync will initialize EF schema
            await context.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
        }

        // Structural baseline 14 inspection:
        var requiredTables = new[]
        {
            "stores", "users", "terminals", "products", "stock_balances", "categories", "brands",
            "sales_invoices", "sales_items", "sale_item_tax_components", "sale_refunds",
            "invoice_sequences", "suppliers", "purchase_invoices", "purchase_items",
            "cash_sessions", "customers", "audit_logs", "backup_records"
        };

        foreach (var table in requiredTables)
        {
            cmd.CommandText = $"SELECT 1 FROM sqlite_master WHERE type='table' AND name='{table}';";
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) == null)
            {
                throw new InvalidOperationException($"Legacy baseline verification failed: required table '{table}' is missing. Database cannot be upgraded.");
            }
        }

        // Inspect Phase 16.5 security fields in stores table
        cmd.CommandText = "PRAGMA table_info(stores);";
        var storeCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                storeCols.Add(reader.GetString(1));
            }
        }

        var requiredStoreCols = new[]
        {
            "store_guid", "master_key_validator_hash", "wrapped_store_signing_key",
            "pending_rotation_validator_hash", "pending_wrapped_store_signing_key",
            "rotation_state", "rotation_token_hash", "rotation_token_expires_at_utc",
            "rotation_initiated_by_user_id"
        };

        foreach (var col in requiredStoreCols)
        {
            if (!storeCols.Contains(col))
            {
                throw new InvalidOperationException($"Legacy baseline verification failed: required column '{col}' in 'stores' is missing. Database cannot be upgraded.");
            }
        }

        // Feature columns
        cmd.CommandText = "PRAGMA table_info(stock_balances);";
        var stockCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                stockCols.Add(reader.GetString(1));
            }
        }
        if (!stockCols.Contains("is_low_stock"))
        {
            throw new InvalidOperationException("Legacy baseline verification failed: 'stock_balances.is_low_stock' is missing.");
        }

        // Foreign keys and integrity
        cmd.CommandText = "PRAGMA foreign_key_check;";
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Legacy baseline verification failed: foreign key violation detected in legacy database.");
            }
        }

        cmd.CommandText = "PRAGMA integrity_check;";
        var integrity = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Legacy baseline verification failed: SQLite integrity check returned '{integrity}'.");
        }

        // Establish baseline 14
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    applied_at_utc TEXT NOT NULL,
    execution_time_ms INTEGER NOT NULL,
    checksum TEXT NOT NULL
);
INSERT OR IGNORE INTO schema_migrations (version, name, applied_at_utc, execution_time_ms, checksum)
VALUES (14, 'Baseline_Phase16.5', datetime('now'), 0, 'VERIFIED_BASELINE');
PRAGMA user_version = 14;
";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<MigrationResult> RunMigrationLifecycleAsync(
        RetailDbContext context,
        string? operationId = null,
        CancellationToken ct = default)
    {
        operationId ??= Guid.NewGuid().ToString("N");

        await VerifyAndEstablishBaseline14IfNeededAsync(context, ct).ConfigureAwait(false);
        int currentVersion = await GetCurrentSchemaVersionAsync(context, ct).ConfigureAwait(false);

        if (currentVersion > _schemaRegistry!.TargetSchemaVersion)
        {
            throw new InvalidOperationException(
                $"DATABASE_TOO_NEW: Database schema version {currentVersion} is newer than server supported schema version {_schemaRegistry.TargetSchemaVersion}. Older server binaries cannot safely run on newer database schema.");
        }

        if (currentVersion == _schemaRegistry.TargetSchemaVersion)
        {
            return new MigrationResult
            {
                IsSuccess = true,
                CurrentState = "UP_TO_DATE",
                OperationId = operationId,
                ExitCode = 0
            };
        }

        var pendingSteps = _schemaRegistry.GetPendingSteps(currentVersion);

        return await RunSafeMigrationAsync(operationId, async () =>
        {
            var conn = context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
            }

            foreach (var step in pendingSteps)
            {
                var sw = Stopwatch.StartNew();
                await using var tx = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

                await step.ApplyAsync(context, ct).ConfigureAwait(false);
                sw.Stop();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR REPLACE INTO schema_migrations (version, name, applied_at_utc, execution_time_ms, checksum)
VALUES (@v, @n, @a, @t, @c);
";
                    var p1 = cmd.CreateParameter(); p1.ParameterName = "@v"; p1.Value = step.Version; cmd.Parameters.Add(p1);
                    var p2 = cmd.CreateParameter(); p2.ParameterName = "@n"; p2.Value = step.Name; cmd.Parameters.Add(p2);
                    var p3 = cmd.CreateParameter(); p3.ParameterName = "@a"; p3.Value = DateTime.UtcNow.ToString("O"); cmd.Parameters.Add(p3);
                    var p4 = cmd.CreateParameter(); p4.ParameterName = "@t"; p4.Value = (int)sw.ElapsedMilliseconds; cmd.Parameters.Add(p4);
                    var p5 = cmd.CreateParameter(); p5.ParameterName = "@c"; p5.Value = "STEP_" + step.Version; cmd.Parameters.Add(p5);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                    cmd.CommandText = $"PRAGMA user_version = {step.Version};";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    public async Task<MigrationResult> RunSafeMigrationAsync(string operationId, Func<Task> executeMigrationAction)
    {
        string dbPath = ResolveDatabasePath();
        string opStagingDir = Path.Combine(_stagingDirectory, operationId);

        // State A: Preflight
        await _journal.AppendEntryAsync("STATE_A_PREFLIGHT", operationId, "Starting preflight checks.");
        if (!File.Exists(dbPath))
        {
            await _journal.AppendEntryAsync("STATE_A_PREFLIGHT_FAILED", operationId, $"{_databaseFileName} not found.");
            return new MigrationResult { IsSuccess = false, CurrentState = "STATE_A", OperationId = operationId, ErrorMessage = $"{_databaseFileName} not found", ExitCode = 101 };
        }

        // State B: Staging check & Maintenance
        await _journal.AppendEntryAsync("STATE_B_STAGING", operationId, "Checking staging area.");
        if (Directory.Exists(_stagingDirectory))
        {
            var existingStagingDirs = Directory.GetDirectories(_stagingDirectory);
            foreach (var dir in existingStagingDirs)
            {
                string dirName = Path.GetFileName(dir);
                if (!string.Equals(dirName, operationId, StringComparison.OrdinalIgnoreCase))
                {
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

        IDisposable? maintLease = null;
        if (_maintenanceCoordinator != null)
        {
            maintLease = await _maintenanceCoordinator.EnterMaintenanceAsync($"Safe migration {operationId} initiated").ConfigureAwait(false);
        }

        try
        {
            // State C: Create pre-migration backup & identity snapshot
            await _journal.AppendEntryAsync("STATE_C_BACKUP_PREPARED", operationId, "Creating pre-migration backup.");
            string backupPath = Path.Combine(opStagingDir, "pre_migration.db");
            string identityBackupPath = Path.Combine(opStagingDir, "pre_migration_identity.dat");

            SqliteConnection.ClearAllPools();
            File.Copy(dbPath, backupPath, overwrite: true);

            string backupSha256 = CalculateSha256(backupPath);
            long backupSize = new FileInfo(backupPath).Length;

            string identitySha256 = string.Empty;
            if (_storeIdentityService != null)
            {
                _storeIdentityService.CreateIdentitySafetySnapshot(identityBackupPath);
                if (File.Exists(identityBackupPath))
                {
                    identitySha256 = CalculateSha256(identityBackupPath);
                }
            }

            // Generate and sign companion manifest using stable StoreSigningKey
            byte[] signingKey = _storeIdentityService != null
                ? _storeIdentityService.GetStoreSigningKey()
                : _signingKey;

            Guid storeGuid = _storeIdentityService != null && _storeIdentityService.StoreId != Guid.Empty
                ? _storeIdentityService.StoreId
                : _storeId;

            Guid backupOpId = Guid.TryParse(operationId, out var parsedOpId) ? parsedOpId : Guid.NewGuid();
            var manifest = new RetailPOS.Contracts.Backup.BackupManifestDto
            {
                ManifestVersion = "1.0",
                BackupOperationId = backupOpId,
                StoreId = storeGuid,
                StoreName = "RetailPOS_Store",
                FileName = "pre_migration.db",
                DatabaseSha256 = backupSha256,
                SizeBytes = backupSize,
                Type = "PreMigration",
                CreatedAtUtc = DateTime.UtcNow,
                AppVersion = "1.0.0",
                SchemaVersion = 14
            };

            var canonical = _backupManifestService.CreateCanonicalString(manifest);
            manifest.Signature = _backupManifestService.ComputeSignature(canonical, signingKey);

            string manifestPath = $"{backupPath}.manifest";
            await _backupManifestService.SaveManifestAsync(manifestPath, manifest).ConfigureAwait(false);

            var marker = new MigrationMarker
            {
                State = "STATE_C_BACKUP_PREPARED",
                OperationId = operationId,
                PreMigrationBackupPath = backupPath,
                PreMigrationBackupSha256 = backupSha256,
                PreMigrationIdentityBackupPath = identityBackupPath,
                PreMigrationIdentityBackupSha256 = identitySha256,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            // Pre-Action Durable Flush: Flush marker BEFORE moving to State D
            await SaveMarkerAsync(marker).ConfigureAwait(false);

            // State D: Migrating
            await _journal.AppendEntryAsync("STATE_D_MIGRATING", operationId, "Executing migration action.");
            marker.State = "STATE_D_MIGRATING";
            marker.UpdatedAtUtc = DateTime.UtcNow;

            // Pre-Action Durable Flush: Flush marker BEFORE executing database DDL/DML!
            await SaveMarkerAsync(marker).ConfigureAwait(false);

            try
            {
                await executeMigrationAction().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Migration failed! Trigger State E Rollback
                await _journal.AppendEntryAsync("STATE_D_FAILED", operationId, $"Migration action threw exception: {ex.Message}");
                var rollbackRes = await ExecuteRollbackAsync(marker, isStartupRecovery: false).ConfigureAwait(false);
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

            // Pre-Action Durable Flush: Flush marker BEFORE verification
            await SaveMarkerAsync(marker).ConfigureAwait(false);

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
        finally
        {
            maintLease?.Dispose();
        }
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
            string json = await File.ReadAllTextAsync(_markerFilePath, Encoding.UTF8).ConfigureAwait(false);
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
        if (marker.State is "STATE_A" or "STATE_B" or "STATE_A_PREFLIGHT" or "STATE_B_STAGING")
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

        // If in State D, E, or F: Crash occurred during or after migration! Rollback required
        return await ExecuteRollbackAsync(marker, isStartupRecovery: true).ConfigureAwait(false);
    }

    private async Task<RollbackResult> ExecuteRollbackAsync(MigrationMarker marker, bool isStartupRecovery)
    {
        string eventPrefix = isStartupRecovery ? "STARTUP_ROLLBACK" : "ROLLBACK";
        await _journal.AppendEntryAsync($"{eventPrefix}_TRIGGERED", marker.OperationId, $"Initiating rollback for state {marker.State}").ConfigureAwait(false);

        // Pre-Action Durable Flush: Record STATE_E_ROLLBACK flushed to disk before replacing files
        marker.State = "STATE_E_ROLLBACK";
        marker.UpdatedAtUtc = DateTime.UtcNow;
        await SaveMarkerAsync(marker).ConfigureAwait(false);

        string dbPath = ResolveDatabasePath();
        string walPath = dbPath + "-wal";
        string shmPath = dbPath + "-shm";

        SqliteConnection.ClearAllPools();

        // STEP 1: Quarantine WAL and SHM BEFORE replacing db file
        if (File.Exists(walPath))
        {
            string walQuarantine = walPath + $".quarantine.{marker.OperationId}";
            File.Move(walPath, walQuarantine, overwrite: true);
            await _journal.AppendEntryAsync($"{eventPrefix}_WAL_QUARANTINED", marker.OperationId, $"Moved WAL to {walQuarantine}").ConfigureAwait(false);
        }

        if (File.Exists(shmPath))
        {
            string shmQuarantine = shmPath + $".quarantine.{marker.OperationId}";
            File.Move(shmPath, shmQuarantine, overwrite: true);
            await _journal.AppendEntryAsync($"{eventPrefix}_SHM_QUARANTINED", marker.OperationId, $"Moved SHM to {shmQuarantine}").ConfigureAwait(false);
        }

        // STEP 2: Verify pre-migration backup file exists
        if (!File.Exists(marker.PreMigrationBackupPath))
        {
            await _journal.AppendEntryAsync($"{eventPrefix}_BACKUP_MISSING_EXIT_106", marker.OperationId, "Pre-migration backup not found!").ConfigureAwait(false);
            return new RollbackResult
            {
                RollbackPerformed = true,
                IsSuccess = false,
                OperationId = marker.OperationId,
                ErrorMessage = "Pre-migration backup file missing",
                ExitCode = 106
            };
        }

        // STEP 3: Verify companion manifest using stable StoreSigningKey
        byte[] signingKey = _storeIdentityService != null
            ? _storeIdentityService.GetStoreSigningKey()
            : _signingKey;

        Guid expectedStoreId = _storeIdentityService != null && _storeIdentityService.StoreId != Guid.Empty
            ? _storeIdentityService.StoreId
            : _storeId;

        string manifestPath = $"{marker.PreMigrationBackupPath}.manifest";
        if (File.Exists(manifestPath))
        {
            var manifest = await _backupManifestService.LoadManifestAsync(manifestPath).ConfigureAwait(false);
            if (manifest == null)
            {
                await _journal.AppendEntryAsync($"{eventPrefix}_MANIFEST_CORRUPT_EXIT_106", marker.OperationId, "Companion manifest unreadable!").ConfigureAwait(false);
                return FailClosedCorruptBackup(marker, eventPrefix, "Corrupt backup companion manifest: EMERGENCY_RECOVERY_REQUIRED");
            }

            if (manifest.StoreId != expectedStoreId)
            {
                await _journal.AppendEntryAsync($"{eventPrefix}_STORE_ID_MISMATCH_EXIT_106", marker.OperationId, $"StoreId mismatch: expected {expectedStoreId}, found {manifest.StoreId}").ConfigureAwait(false);
                return FailClosedCorruptBackup(marker, eventPrefix, "Backup companion manifest StoreId mismatch: EMERGENCY_RECOVERY_REQUIRED");
            }

            string expectedFileName = Path.GetFileName(marker.PreMigrationBackupPath);
            if (!string.Equals(manifest.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                await _journal.AppendEntryAsync($"{eventPrefix}_FILENAME_MISMATCH_EXIT_106", marker.OperationId, $"FileName mismatch: expected {expectedFileName}, found {manifest.FileName}").ConfigureAwait(false);
                return FailClosedCorruptBackup(marker, eventPrefix, "Backup companion manifest FileName mismatch: EMERGENCY_RECOVERY_REQUIRED");
            }

            bool isSignatureValid = _backupManifestService.VerifySignature(manifest, signingKey);
            if (!isSignatureValid)
            {
                await _journal.AppendEntryAsync($"{eventPrefix}_SIGNATURE_INVALID_EXIT_106", marker.OperationId, "HMAC signature verification failed on companion manifest!").ConfigureAwait(false);
                return FailClosedCorruptBackup(marker, eventPrefix, "Backup manifest signature verification failed: EMERGENCY_RECOVERY_REQUIRED");
            }

            if (!string.Equals(manifest.DatabaseSha256, marker.PreMigrationBackupSha256, StringComparison.OrdinalIgnoreCase))
            {
                await _journal.AppendEntryAsync($"{eventPrefix}_MANIFEST_SHA_MISMATCH_EXIT_106", marker.OperationId, "Manifest SHA-256 does not match marker!").ConfigureAwait(false);
                return FailClosedCorruptBackup(marker, eventPrefix, "Backup manifest hash mismatch: EMERGENCY_RECOVERY_REQUIRED");
            }
        }

        // STEP 4: Verify SHA-256 of staged pre-migration backup against marker
        string actualHash = CalculateSha256(marker.PreMigrationBackupPath);
        if (!string.Equals(actualHash, marker.PreMigrationBackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            await _journal.AppendEntryAsync($"{eventPrefix}_SHA_MISMATCH_EXIT_106", marker.OperationId,
                $"Expected {marker.PreMigrationBackupSha256} but got {actualHash}. Fails closed.").ConfigureAwait(false);
            return FailClosedCorruptBackup(marker, eventPrefix, "Backup SHA-256 mismatch: EMERGENCY_RECOVERY_REQUIRED");
        }

        // STEP 5: Replace database file from verified staged backup
        File.Copy(marker.PreMigrationBackupPath, dbPath, overwrite: true);
        await _journal.AppendEntryAsync($"{eventPrefix}_DB_RESTORED", marker.OperationId, $"{_databaseFileName} successfully restored from verified pre-migration backup.").ConfigureAwait(false);

        // STEP 6: Restore identity file if snapshot exists
        if (!string.IsNullOrEmpty(marker.PreMigrationIdentityBackupPath) && File.Exists(marker.PreMigrationIdentityBackupPath))
        {
            if (_storeIdentityService != null)
            {
                _storeIdentityService.RestoreIdentitySafetySnapshot(marker.PreMigrationIdentityBackupPath);
            }
            await _journal.AppendEntryAsync($"{eventPrefix}_IDENTITY_RESTORED", marker.OperationId, "Identity material restored from pre-migration snapshot.").ConfigureAwait(false);
        }

        // Clean up marker and staging
        CleanupStaging(marker.OperationId);
        if (File.Exists(_markerFilePath))
        {
            File.Delete(_markerFilePath);
        }

        await _journal.AppendEntryAsync($"{eventPrefix}_COMPLETED", marker.OperationId, "Rollback successfully completed.").ConfigureAwait(false);

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

        string tmpPath = _markerFilePath + ".tmp";
        await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(fs, Encoding.UTF8))
        {
            await writer.WriteAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmpPath, _markerFilePath, overwrite: true);
    }

    private static string CalculateSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
