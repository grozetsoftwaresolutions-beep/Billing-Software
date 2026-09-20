namespace RetailPOS.Infrastructure.Backup;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Contracts.Common;
using RetailPOS.Infrastructure.Persistence;

public sealed class BackupService : IBackupService
{
    private static readonly Regex SafeBasenameRegex = new(@"^[a-zA-Z0-9_\-\.]+\.db$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly IBackupRepository _backupRepo;
    private readonly IBackupSettingsStore _settingsStore;
    private readonly IBackupRetentionService _retentionService;
    private readonly IDatabaseMaintenanceCoordinator _maintenanceCoordinator;
    private readonly IBackupOperationCoordinator _opCoordinator;
    private readonly IMaintenanceScopeFactory _scopeFactory;
    private readonly IStoreIdentityService _storeIdentity;
    private readonly IBackupManifestService _manifestService;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly RetailDbContext _dbContext;
    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly string _dataDir;
    private readonly string _quarantineDir;
    private readonly string _recoveryMarkerPath;
    private readonly string _drRecoveryMarkerPath;

    public BackupService(
        IBackupRepository backupRepo,
        IBackupSettingsStore settingsStore,
        IBackupRetentionService retentionService,
        IDatabaseMaintenanceCoordinator maintenanceCoordinator,
        IBackupOperationCoordinator opCoordinator,
        IMaintenanceScopeFactory scopeFactory,
        IStoreIdentityService storeIdentity,
        IBackupManifestService manifestService,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork,
        RetailDbContext dbContext,
        IServiceScopeFactory? serviceScopeFactory = null,
        string? customDataDir = null,
        string? customQuarantineDir = null,
        string? customRecoveryMarkerPath = null,
        string? customDrRecoveryMarkerPath = null)
    {
        _backupRepo = backupRepo;
        _settingsStore = settingsStore;
        _retentionService = retentionService;
        _maintenanceCoordinator = maintenanceCoordinator;
        _opCoordinator = opCoordinator;
        _scopeFactory = scopeFactory;
        _storeIdentity = storeIdentity;
        _manifestService = manifestService;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
        _dbContext = dbContext;
        _serviceScopeFactory = serviceScopeFactory;

        _dataDir = customDataDir ?? RetailPOSPaths.GetDefaultDatabaseDirectory();
        _quarantineDir = customQuarantineDir ?? Path.Combine(RetailPOSPaths.GetDefaultStagingDirectory(), "Quarantine");
        var recoveryDir = Path.Combine(RetailPOSPaths.GetDefaultStagingDirectory(), "Recovery");
        _recoveryMarkerPath = customRecoveryMarkerPath ?? Path.Combine(recoveryDir, "restore_state.json");
        _drRecoveryMarkerPath = customDrRecoveryMarkerPath ?? Path.Combine(recoveryDir, "dr_recovery_state.json");
    }

    public async Task<BackupRecordDto> CreateBackupAsync(
        BackupType type,
        string triggeredByUserId,
        string triggeredByUserName,
        string? reason = null,
        CancellationToken ct = default)
    {
        using var opLock = await _opCoordinator.AcquireExclusiveLockAsync($"BackupCreation_{type}", ct).ConfigureAwait(false);

        var settings = await _settingsStore.GetSettingsAsync(ct).ConfigureAwait(false);
        var targetDir = !string.IsNullOrWhiteSpace(settings.BackupStoragePath)
            ? settings.BackupStoragePath
            : RetailPOSPaths.GetDefaultBackupsDirectory();

        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
        if (!Directory.Exists(_quarantineDir)) Directory.CreateDirectory(_quarantineDir);

        // Pre-flight disk space check: target free space must be >= 2.5x (DB + WAL size)
        var primaryDbPath = Path.Combine(_dataDir, "grozet_pos.db");
        long dbSizeBytes = 0;
        if (File.Exists(primaryDbPath))
        {
            dbSizeBytes += new FileInfo(primaryDbPath).Length;
            var walPath = $"{primaryDbPath}-wal";
            if (File.Exists(walPath)) dbSizeBytes += new FileInfo(walPath).Length;
        }
        long requiredDiskSpace = (long)(dbSizeBytes * 2.5);
        if (requiredDiskSpace > 0)
        {
            CheckTargetDiskSpace(targetDir, requiredDiskSpace);
            CheckTargetDiskSpace(_quarantineDir, requiredDiskSpace);
        }

        var backupOpId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var typeStr = type.ToString().ToLowerInvariant();
        var fileName = $"grozet_pos_{typeStr}_{timestamp}_{backupOpId:N}.db";

        var quarantineDbPath = Path.Combine(_quarantineDir, $"quarantine_backup_{backupOpId:N}.tmp");
        var quarantineManifestPath = Path.Combine(_quarantineDir, $"quarantine_manifest_{backupOpId:N}.tmp");

        var authoritativeDbPath = Path.Combine(targetDir, fileName);
        var authoritativeManifestPath = $"{authoritativeDbPath}.manifest";

        try
        {
            // Step 5: VACUUM INTO quarantineDbPath
            var conn = _dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
            }

            int currentSchemaVersion = 15;
            using (var verCmd = conn.CreateCommand())
            {
                verCmd.CommandText = "PRAGMA user_version;";
                var verVal = await verCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (verVal != null) currentSchemaVersion = Convert.ToInt32(verVal, CultureInfo.InvariantCulture);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"VACUUM INTO '{quarantineDbPath.Replace("'", "''")}';";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Step 6: Hash & size computation
            var fi = new FileInfo(quarantineDbPath);
            var sizeBytes = fi.Length;
            string sha256;
            using (var s = File.OpenRead(quarantineDbPath))
            {
                var hashBytes = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
                sha256 = Convert.ToHexString(hashBytes).ToUpperInvariant();
            }

            // Step 7: Verify SQLite integrity of quarantine file
            ValidateSqliteIntegrity(quarantineDbPath);

            // Step 8: Generate companion manifest
            var signingKey = _storeIdentity.GetStoreSigningKey();
            var manifest = new BackupManifestDto
            {
                ManifestVersion = "1.0",
                BackupOperationId = backupOpId,
                StoreId = _storeIdentity.StoreId,
                StoreName = _storeIdentity.StoreName,
                FileName = fileName,
                DatabaseSha256 = sha256,
                SizeBytes = sizeBytes,
                Type = type.ToString(),
                CreatedAtUtc = DateTime.UtcNow,
                AppVersion = "1.0.0-phase16.7",
                SchemaVersion = currentSchemaVersion
            };
            var canonical = _manifestService.CreateCanonicalString(manifest);
            manifest.Signature = _manifestService.ComputeSignature(canonical, signingKey);
            await _manifestService.SaveManifestAsync(quarantineManifestPath, manifest, ct).ConfigureAwait(false);

            // Step 9 & 10: Two-file publication to authoritative dir
            File.Move(quarantineDbPath, authoritativeDbPath, overwrite: false);
            File.Move(quarantineManifestPath, authoritativeManifestPath, overwrite: false);

            // Step 11: Register backup_history
            var record = new BackupRecord
            {
                BackupOperationId = backupOpId,
                FileName = fileName,
                FilePath = authoritativeDbPath,
                SizeBytes = sizeBytes,
                DatabaseSha256 = sha256,
                Type = type,
                Status = BackupStatus.Succeeded,
                FileState = FileState.Active,
                Origin = BackupOrigin.LocalGenerated,
                StoreId = _storeIdentity.StoreId,
                StoreName = _storeIdentity.StoreName,
                AppVersion = "1.0.0-phase16.7",
                SchemaVersion = currentSchemaVersion,
                CreatedAtUtc = manifest.CreatedAtUtc,
                CompletedAtUtc = DateTime.UtcNow,
                TriggeredByUserId = triggeredByUserId,
                TriggeredByUserName = triggeredByUserName
            };

            await _backupRepo.InsertAsync(record, ct).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            // Step 12: Prune retention in background using isolated DI scope
            if (_serviceScopeFactory != null)
            {
                var scopeFactory = _serviceScopeFactory;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var scopedRetentionService = scope.ServiceProvider.GetRequiredService<IBackupRetentionService>();
                        await scopedRetentionService.PruneAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Background retention failure must not crash host
                    }
                }, CancellationToken.None);
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try { await _retentionService.PruneAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                }, CancellationToken.None);
            }

            // Step 13: Mirror to secondary / USB storage if configured
            if (!string.IsNullOrWhiteSpace(settings.SecondaryStoragePath) || !string.IsNullOrWhiteSpace(settings.RemovableUsbBackupPath))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await MirrorBackupToSecondaryAsync(fileName, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                }, CancellationToken.None);
            }

            // Step 14: Audit log
            await _auditRepo.RecordAsync(
                nameof(BackupRecord),
                "BACKUP_CREATED",
                triggeredByUserName,
                $"Backup created: {fileName}, Type: {type}, Size: {sizeBytes} bytes",
                ct).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);


            return MapToDto(record);
        }
        catch (Exception ex)
        {
            if (File.Exists(quarantineDbPath)) try { File.Delete(quarantineDbPath); } catch { }
            if (File.Exists(quarantineManifestPath)) try { File.Delete(quarantineManifestPath); } catch { }
            throw new DomainException($"Backup creation failed: {ex.Message}", "BACKUP_CREATION_FAILED");
        }
    }

    public async Task<BackupRecordDto> IngestExternalBackupAsync(
        Stream dbStream,
        string dbFileName,
        Stream manifestStream,
        string manifestFileName,
        string userId,
        string userName,
        CancellationToken ct = default)
    {
        using var opLock = await _opCoordinator.AcquireExclusiveLockAsync("ExternalBackupIngest", ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(dbFileName) ||
            dbFileName.Contains('/') ||
            dbFileName.Contains('\\') ||
            dbFileName.Contains("..") ||
            dbFileName.Contains(':'))
        {
            throw new DomainException($"Invalid backup filename '{dbFileName}'. Directory separators and path traversal sequences are strictly forbidden.", "INVALID_FILENAME");
        }

        var safeBasename = Path.GetFileName(dbFileName);
        if (string.IsNullOrWhiteSpace(safeBasename) || !SafeBasenameRegex.IsMatch(safeBasename) || safeBasename.Length > 128)
        {
            throw new DomainException($"Invalid backup filename '{dbFileName}'. Must match safe basename pattern.", "INVALID_FILENAME");
        }

        if (!Directory.Exists(_quarantineDir)) Directory.CreateDirectory(_quarantineDir);

        var uploadId = Guid.NewGuid();
        var qDbPath = Path.Combine(_quarantineDir, $"quarantine_upload_db_{uploadId:N}.tmp");
        var qManifestPath = Path.Combine(_quarantineDir, $"quarantine_upload_manifest_{uploadId:N}.tmp");

        try
        {
            using (var fs = File.Create(qDbPath))
            {
                await dbStream.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            using (var fs = File.Create(qManifestPath))
            {
                await manifestStream.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            var manifest = await _manifestService.LoadManifestAsync(qManifestPath, ct).ConfigureAwait(false);
            if (manifest == null)
            {
                throw new DomainException("Manifest file is empty or invalid JSON.", "INVALID_MANIFEST");
            }

            if (!string.Equals(manifest.FileName, safeBasename, StringComparison.Ordinal))
            {
                throw new DomainException($"Manifest FileName '{manifest.FileName}' does not match upload '{safeBasename}'.", "MANIFEST_FILENAME_MISMATCH");
            }

            if (manifest.StoreId != _storeIdentity.StoreId)
            {
                throw new DomainException($"StoreId mismatch. Manifest belongs to '{manifest.StoreId}', current store is '{_storeIdentity.StoreId}'.", "STORE_ID_MISMATCH");
            }

            var signingKey = _storeIdentity.GetStoreSigningKey();
            if (!_manifestService.VerifySignature(manifest, signingKey))
            {
                throw new DomainException("HMAC signature verification failed for companion manifest.", "INVALID_SIGNATURE");
            }

            var fi = new FileInfo(qDbPath);
            if (fi.Length != manifest.SizeBytes)
            {
                throw new DomainException($"Physical byte length ({fi.Length}) does not match manifest ({manifest.SizeBytes}).", "SIZE_MISMATCH");
            }

            string computedSha;
            using (var s = File.OpenRead(qDbPath))
            {
                var hashBytes = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
                computedSha = Convert.ToHexString(hashBytes).ToUpperInvariant();
            }

            if (!string.Equals(computedSha, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException("Database SHA-256 does not match manifest.", "CHECKSUM_MISMATCH");
            }

            ValidateSqliteIntegrity(qDbPath);

            var settings = await _settingsStore.GetSettingsAsync(ct).ConfigureAwait(false);
            var targetDir = !string.IsNullOrWhiteSpace(settings.BackupStoragePath)
                ? settings.BackupStoragePath
                : RetailPOSPaths.GetDefaultBackupsDirectory();

            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            var authoritativeDbPath = Path.Combine(targetDir, safeBasename);
            var authoritativeManifestPath = $"{authoritativeDbPath}.manifest";

            // Collision check
            if (File.Exists(authoritativeDbPath))
            {
                string existingSha;
                using (var s = File.OpenRead(authoritativeDbPath))
                {
                    var hashBytes = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
                    existingSha = Convert.ToHexString(hashBytes).ToUpperInvariant();
                }

                if (!string.Equals(existingSha, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DomainException($"BACKUP_FILENAME_COLLISION: A backup named '{safeBasename}' already exists with different contents.", "BACKUP_FILENAME_COLLISION");
                }

                // Existing has same SHA-256. Check if companion manifest exists and is fully valid!
                bool existingPairValid = false;
                BackupManifestDto? existingManifest = null;

                if (File.Exists(authoritativeManifestPath))
                {
                    existingManifest = await _manifestService.LoadManifestAsync(authoritativeManifestPath, ct).ConfigureAwait(false);
                    if (existingManifest != null &&
                        string.Equals(existingManifest.FileName, safeBasename, StringComparison.Ordinal) &&
                        existingManifest.StoreId == _storeIdentity.StoreId &&
                        _manifestService.VerifySignature(existingManifest, signingKey) &&
                        string.Equals(existingManifest.DatabaseSha256, existingSha, StringComparison.OrdinalIgnoreCase) &&
                        new FileInfo(authoritativeDbPath).Length == existingManifest.SizeBytes &&
                        VerifySqliteHeader(authoritativeDbPath))
                    {
                        try
                        {
                            ValidateSqliteIntegrity(authoritativeDbPath);
                            existingPairValid = true;
                        }
                        catch
                        {
                            existingPairValid = false;
                        }
                    }
                }

                if (existingPairValid && existingManifest != null)
                {
                    // Check immutable identity:
                    if (existingManifest.BackupOperationId == manifest.BackupOperationId &&
                        existingManifest.CreatedAtUtc == manifest.CreatedAtUtc)
                    {
                        // Idempotent success (200 OK)
                        var existingRec = await _backupRepo.GetByFileNameAsync(safeBasename, ct).ConfigureAwait(false);
                        if (existingRec != null)
                        {
                            return MapToDto(existingRec);
                        }
                    }
                    else
                    {
                        // Different immutable identity! Collision rejection, preserve existing valid backup
                        throw new DomainException("BACKUP_IDENTITY_COLLISION: An existing valid backup with the same checksum exists under a different operation identity.", "BACKUP_IDENTITY_COLLISION");
                    }
                }
                else
                {
                    // Existing pair was corrupt or incomplete. Remove invalid existing artifact
                    try { File.Delete(authoritativeDbPath); } catch { }
                    if (File.Exists(authoritativeManifestPath)) try { File.Delete(authoritativeManifestPath); } catch { }
                }
            }

            // Publish
            File.Move(qDbPath, authoritativeDbPath, overwrite: true);
            File.Move(qManifestPath, authoritativeManifestPath, overwrite: true);

            var record = new BackupRecord
            {
                BackupOperationId = manifest.BackupOperationId != Guid.Empty ? manifest.BackupOperationId : Guid.NewGuid(),
                FileName = safeBasename,
                FilePath = authoritativeDbPath,
                SizeBytes = manifest.SizeBytes,
                DatabaseSha256 = manifest.DatabaseSha256,
                Type = Enum.TryParse<BackupType>(manifest.Type, out var t) ? t : BackupType.Manual,
                Status = BackupStatus.Succeeded,
                FileState = FileState.Active,
                Origin = BackupOrigin.UploadedExternal,
                StoreId = manifest.StoreId,
                StoreName = manifest.StoreName,
                AppVersion = manifest.AppVersion,
                SchemaVersion = manifest.SchemaVersion,
                CreatedAtUtc = manifest.CreatedAtUtc,
                CompletedAtUtc = DateTime.UtcNow,
                TriggeredByUserId = userId,
                TriggeredByUserName = userName
            };

            await _backupRepo.InsertAsync(record, ct).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            return MapToDto(record);
        }
        finally
        {
            if (File.Exists(qDbPath)) try { File.Delete(qDbPath); } catch { }
            if (File.Exists(qManifestPath)) try { File.Delete(qManifestPath); } catch { }
        }
    }

    public async Task<BackupRecordDto> RestoreBackupAsync(
        string fileName,
        string triggeredByUserId,
        string triggeredByUserName,
        CancellationToken ct = default)
    {
        using var opLock = await _opCoordinator.AcquireExclusiveLockAsync("BackupRestore", ct).ConfigureAwait(false);

        var safeBasename = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeBasename) || !SafeBasenameRegex.IsMatch(safeBasename))
        {
            throw new DomainException("Invalid backup filename.", "INVALID_FILENAME");
        }

        var settings = await _settingsStore.GetSettingsAsync(ct).ConfigureAwait(false);
        var targetDir = !string.IsNullOrWhiteSpace(settings.BackupStoragePath)
            ? settings.BackupStoragePath
            : RetailPOSPaths.GetDefaultBackupsDirectory();

        var targetDbPath = Path.Combine(targetDir, safeBasename);
        var targetManifestPath = $"{targetDbPath}.manifest";

        if (!File.Exists(targetDbPath) || !File.Exists(targetManifestPath))
        {
            throw new DomainException($"Backup artifact pair not found: {safeBasename}", "BACKUP_NOT_FOUND");
        }

        var signingKey = _storeIdentity.GetStoreSigningKey();
        var manifest = await _manifestService.LoadManifestAsync(targetManifestPath, ct).ConfigureAwait(false);
        if (manifest == null || !_manifestService.VerifySignature(manifest, signingKey))
        {
            throw new DomainException("Manifest verification failed for restore candidate.", "BACKUP_VALIDATION_FAILED");
        }

        ValidateSqliteIntegrity(targetDbPath);

        // Enter maintenance mode
        using var maintSession = await _maintenanceCoordinator.EnterMaintenanceAsync("Database restore initiated", ct).ConfigureAwait(false);

        var restoreOpId = Guid.NewGuid();
        var primaryDbPath = Path.Combine(_dataDir, "grozet_pos.db");
        var primaryWalPath = $"{primaryDbPath}-wal";
        var primaryShmPath = $"{primaryDbPath}-shm";
        var rollbackDbPath = $"{primaryDbPath}.rollback_staging";
        var rollbackWalPath = $"{primaryDbPath}-wal.rollback_staging";
        var tmpInstallPath = $"{primaryDbPath}.tmp_install";

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var safetySnapshotPath = Path.Combine(targetDir, $"grozet_pos_safety_snapshot_{timestamp}_{restoreOpId:N}.db");
        var identitySafetySnapshotPath = Path.Combine(targetDir, $"store_identity_safety_snapshot_{timestamp}_{restoreOpId:N}.dat");
        var identityRollbackPath = Path.Combine(RetailPOSPaths.GetDefaultSecurityDirectory(), "store_identity.dat.rollback_staging");

        // Write marker: Prepared
        await WriteMarkerAsync(restoreOpId, "Prepared", safetySnapshotPath, targetDbPath, identitySafetySnapshotPath, ct).ConfigureAwait(false);

        // Create & verify Dual Safety Snapshot (both grozet_pos.db AND store_identity.dat)
        if (File.Exists(primaryDbPath))
        {
            File.Copy(primaryDbPath, safetySnapshotPath, overwrite: true);
            ValidateSqliteIntegrity(safetySnapshotPath);
        }

        _storeIdentity.CreateIdentitySafetySnapshot(identitySafetySnapshotPath);
        _storeIdentity.CreateIdentitySafetySnapshot(identityRollbackPath);

        // Write marker: SafetySnapshotCreated
        await WriteMarkerAsync(restoreOpId, "SafetySnapshotCreated", safetySnapshotPath, targetDbPath, identitySafetySnapshotPath, ct).ConfigureAwait(false);

        // Close pools and stage DB
        SqliteConnection.ClearAllPools();

        if (File.Exists(primaryDbPath))
        {
            File.Move(primaryDbPath, rollbackDbPath, overwrite: true);
        }
        if (File.Exists(primaryWalPath))
        {
            File.Move(primaryWalPath, rollbackWalPath, overwrite: true);
        }
        if (File.Exists(primaryShmPath))
        {
            try { File.Delete(primaryShmPath); } catch { }
        }

        // Write marker: ProductionDbStaged
        await WriteMarkerAsync(restoreOpId, "ProductionDbStaged", safetySnapshotPath, targetDbPath, identitySafetySnapshotPath, ct).ConfigureAwait(false);

        // Copy candidate to tmp_install and rename to grozet_pos.db
        File.Copy(targetDbPath, tmpInstallPath, overwrite: true);
        File.Move(tmpInstallPath, primaryDbPath, overwrite: true);

        // Write marker: TargetInstalled
        await WriteMarkerAsync(restoreOpId, "TargetInstalled", safetySnapshotPath, targetDbPath, identitySafetySnapshotPath, ct).ConfigureAwait(false);

        try
        {
            // Run migrations & checks under maintenance scope
            using (_scopeFactory.CreateMaintenanceScope())
            {
                await WriteMarkerAsync(restoreOpId, "MigrationStarted", safetySnapshotPath, targetDbPath, identitySafetySnapshotPath, ct).ConfigureAwait(false);
                await _dbContext.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
                await DatabaseInitializer.UpgradeSchemaIfNeededAsync(_dbContext, ct).ConfigureAwait(false);
                await WriteMarkerAsync(restoreOpId, "MigrationCompleted", safetySnapshotPath, targetDbPath, identitySafetySnapshotPath, ct).ConfigureAwait(false);

                await WriteMarkerAsync(restoreOpId, "VerificationStarted", safetySnapshotPath, targetDbPath, identitySafetySnapshotPath, ct).ConfigureAwait(false);
                ValidateSqliteIntegrity(primaryDbPath);
                await WriteMarkerAsync(restoreOpId, "VerificationCompleted", safetySnapshotPath, targetDbPath, identitySafetySnapshotPath, ct).ConfigureAwait(false);

                // Reload active identity from current or restored state
                await _storeIdentity.ReloadActiveIdentityAsync(ct).ConfigureAwait(false);

                // Idempotently insert audit record
                var audit = new AuditLog(
                    0,
                    "DATABASE_RESTORED",
                    nameof(BackupRecord),
                    fileName,
                    Guid.TryParse(triggeredByUserId, out var uid) ? uid : null,
                    null,
                    $"Database successfully restored from {fileName}. RestoreOpId: {restoreOpId}",
                    true);

                try
                {
                    await _dbContext.AuditLogs.AddAsync(audit, ct).ConfigureAwait(false);
                    await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // Database-enforced unique index IX_audit_logs_database_restored guarantees idempotency
                }
            }

            // Cleanup rollback staging and marker on success
            if (File.Exists(rollbackDbPath)) try { File.Delete(rollbackDbPath); } catch { }
            if (File.Exists(rollbackWalPath)) try { File.Delete(rollbackWalPath); } catch { }
            if (File.Exists(identityRollbackPath)) try { File.Delete(identityRollbackPath); } catch { }
            if (File.Exists(_recoveryMarkerPath)) try { File.Delete(_recoveryMarkerPath); } catch { }

            var restoredRec = await _backupRepo.GetByFileNameAsync(safeBasename, ct).ConfigureAwait(false);
            if (restoredRec != null)
            {
                return MapToDto(restoredRec);
            }

            return new BackupRecordDto
            {
                BackupOperationId = restoreOpId,
                FileName = safeBasename,
                FilePath = targetDbPath,
                SizeBytes = manifest.SizeBytes,
                DatabaseSha256 = manifest.DatabaseSha256,
                Type = manifest.Type,
                Status = "Succeeded",
                FileState = "Active",
                Origin = "LocalGenerated",
                StoreId = manifest.StoreId,
                StoreName = manifest.StoreName,
                AppVersion = manifest.AppVersion,
                SchemaVersion = manifest.SchemaVersion,
                CreatedAtUtc = manifest.CreatedAtUtc,
                CompletedAtUtc = DateTime.UtcNow,
                TriggeredByUserId = triggeredByUserId,
                TriggeredByUserName = triggeredByUserName
            };
        }
        catch (Exception ex)
        {
            // Atomic Dual Rollback: restore both DB and store_identity.dat
            SqliteConnection.ClearAllPools();

            if (File.Exists(primaryDbPath)) try { File.Delete(primaryDbPath); } catch { }
            if (File.Exists(primaryWalPath)) try { File.Delete(primaryWalPath); } catch { }
            if (File.Exists(primaryShmPath)) try { File.Delete(primaryShmPath); } catch { }

            if (File.Exists(rollbackDbPath))
            {
                File.Move(rollbackDbPath, primaryDbPath, overwrite: true);
                if (File.Exists(rollbackWalPath))
                {
                    File.Move(rollbackWalPath, primaryWalPath, overwrite: true);
                }
            }
            else if (File.Exists(safetySnapshotPath))
            {
                File.Copy(safetySnapshotPath, primaryDbPath, overwrite: true);
            }

            if (File.Exists(identityRollbackPath))
            {
                _storeIdentity.RestoreIdentitySafetySnapshot(identityRollbackPath);
                try { File.Delete(identityRollbackPath); } catch { }
            }
            else if (File.Exists(identitySafetySnapshotPath))
            {
                _storeIdentity.RestoreIdentitySafetySnapshot(identitySafetySnapshotPath);
            }

            throw new DomainException($"Restore failed and both database and identity were rolled back to pre-restore state: {ex.Message}", "RESTORE_FAILED");
        }
    }

    public async Task EmergencyRestoreAsync(
        string fileName,
        char[] masterRecoveryKeyHex,
        CancellationToken ct = default)
    {
        if (masterRecoveryKeyHex == null || masterRecoveryKeyHex.Length != 64)
        {
            throw new DomainException("StoreMasterRecoveryKey must be a 64-character hexadecimal string.", "INVALID_MASTER_KEY");
        }

        var store = await _dbContext.Stores.AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (store != null && !string.IsNullOrWhiteSpace(store.MasterKeyValidatorHash))
        {
            var storeGuid = store.StoreGuid != Guid.Empty ? store.StoreGuid : _storeIdentity.StoreId;
            if (!_storeIdentity.ValidateMasterRecoveryKeyAgainstVerifier(storeGuid, masterRecoveryKeyHex, store.MasterKeyValidatorHash))
            {
                throw new DomainException("Invalid StoreMasterRecoveryKey.", "INVALID_MASTER_KEY");
            }
        }
        else if (!_storeIdentity.ValidateMasterRecoveryKey(masterRecoveryKeyHex))
        {
            throw new DomainException("Invalid StoreMasterRecoveryKey.", "INVALID_MASTER_KEY");
        }

        using (_scopeFactory.CreateMaintenanceScope())
        {
            await RestoreBackupAsync(fileName, "EMERGENCY_RECOVERY_CLI", "Offline Administrator", ct).ConfigureAwait(false);
        }
    }


    public async Task<IReadOnlyList<BackupRecordDto>> GetBackupHistoryAsync(CancellationToken ct = default)
    {
        var records = await _backupRepo.GetAllAsync(ct).ConfigureAwait(false);
        var dtos = new List<BackupRecordDto>();
        foreach (var r in records) dtos.Add(MapToDto(r));
        return dtos;
    }

    public async Task<BackupRecordDto?> GetBackupByIdAsync(Guid operationId, CancellationToken ct = default)
    {
        var record = await _backupRepo.GetByIdAsync(operationId, ct).ConfigureAwait(false);
        return record != null ? MapToDto(record) : null;
    }

    public async Task<BackupIntegrityReportDto> VerifyBackupAsync(string fileName, CancellationToken ct = default)
    {
        var safeBasename = Path.GetFileName(fileName);
        var settings = await _settingsStore.GetSettingsAsync(ct).ConfigureAwait(false);
        var targetDir = !string.IsNullOrWhiteSpace(settings.BackupStoragePath)
            ? settings.BackupStoragePath
            : RetailPOSPaths.GetDefaultBackupsDirectory();

        var dbPath = Path.Combine(targetDir, safeBasename);
        var manifestPath = $"{dbPath}.manifest";

        var report = new BackupIntegrityReportDto { FileName = safeBasename };

        if (!File.Exists(dbPath))
        {
            report.Details = "Database file does not exist.";
            return report;
        }

        if (!File.Exists(manifestPath))
        {
            report.Details = "Companion manifest does not exist.";
            return report;
        }

        var signingKey = _storeIdentity.GetStoreSigningKey();
        var manifest = await _manifestService.LoadManifestAsync(manifestPath, ct).ConfigureAwait(false);
        if (manifest == null || !_manifestService.VerifySignature(manifest, signingKey))
        {
            report.Details = "Manifest signature is invalid.";
            return report;
        }
        report.ManifestValid = true;

        using (var s = File.OpenRead(dbPath))
        {
            var hashBytes = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
            var sha = Convert.ToHexString(hashBytes).ToUpperInvariant();
            if (!string.Equals(sha, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                report.Details = "Database checksum mismatch.";
                return report;
            }
        }
        report.ChecksumValid = true;

        try
        {
            ValidateSqliteIntegrity(dbPath);
            report.SqliteIntegrityValid = true;
            report.ForeignKeyCheckValid = true;
            report.IsValid = true;
            report.Details = "Integrity check passed.";
        }
        catch (Exception ex)
        {
            report.Details = ex.Message;
        }

        return report;
    }

    public async Task<BackupSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        var config = await _settingsStore.GetSettingsAsync(ct).ConfigureAwait(false);
        return new BackupSettingsDto
        {
            DailyBackupEnabled = config.DailyBackupEnabled,
            DailyBackupTime = config.DailyBackupTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
            HourlyBackupEnabled = config.HourlyBackupEnabled,
            HourlyIntervalHours = config.HourlyIntervalHours,
            RetentionDailyCount = config.RetentionDailyCount,
            RetentionHourlyCount = config.RetentionHourlyCount,
            RetentionSnapshotCount = config.RetentionSnapshotCount,
            BackupStoragePath = config.BackupStoragePath,
            SecondaryStoragePath = config.SecondaryStoragePath,
            RemovableUsbBackupPath = config.RemovableUsbBackupPath,
            MaxStorageCeilingBytes = config.MaxStorageCeilingBytes
        };
    }

    public async Task<BackupSettingsDto> UpdateSettingsAsync(UpdateBackupSettingsDto updateDto, CancellationToken ct = default)
    {
        var current = await _settingsStore.GetSettingsAsync(ct).ConfigureAwait(false);
        current.DailyBackupEnabled = updateDto.DailyBackupEnabled;
        if (TimeSpan.TryParse(updateDto.DailyBackupTime, CultureInfo.InvariantCulture, out var ts))
        {
            current.DailyBackupTime = ts;
        }
        current.HourlyBackupEnabled = updateDto.HourlyBackupEnabled;
        current.HourlyIntervalHours = updateDto.HourlyIntervalHours;
        current.RetentionDailyCount = updateDto.RetentionDailyCount;
        current.RetentionHourlyCount = updateDto.RetentionHourlyCount;
        current.RetentionSnapshotCount = updateDto.RetentionSnapshotCount;
        if (!string.IsNullOrWhiteSpace(updateDto.BackupStoragePath))
        {
            current.BackupStoragePath = updateDto.BackupStoragePath;
        }
        if (updateDto.SecondaryStoragePath != null)
        {
            current.SecondaryStoragePath = updateDto.SecondaryStoragePath;
        }
        if (updateDto.RemovableUsbBackupPath != null)
        {
            current.RemovableUsbBackupPath = updateDto.RemovableUsbBackupPath;
        }
        if (updateDto.MaxStorageCeilingBytes.HasValue)
        {
            current.MaxStorageCeilingBytes = updateDto.MaxStorageCeilingBytes.Value;
        }

        await _settingsStore.SaveSettingsAsync(current, ct).ConfigureAwait(false);
        return await GetSettingsAsync(ct).ConfigureAwait(false);
    }

    public async Task MirrorBackupToSecondaryAsync(string backupFileName, CancellationToken ct = default)
    {
        var settings = await _settingsStore.GetSettingsAsync(ct).ConfigureAwait(false);
        var primaryDir = !string.IsNullOrWhiteSpace(settings.BackupStoragePath)
            ? settings.BackupStoragePath
            : RetailPOSPaths.GetDefaultBackupsDirectory();
        var primaryDb = Path.Combine(primaryDir, backupFileName);
        var primaryManifest = $"{primaryDb}.manifest";

        if (!File.Exists(primaryDb) || !File.Exists(primaryManifest))
        {
            return;
        }

        // 1. Class B1: Secondary Internal Disk Backup (Plaintext SQLite + companion manifest)
        if (!string.IsNullOrWhiteSpace(settings.SecondaryStoragePath))
        {
            try
            {
                var secQuarantine = Path.Combine(settings.SecondaryStoragePath, "Staging", "Quarantine");
                var secBackups = Path.Combine(settings.SecondaryStoragePath, "Backups");
                Directory.CreateDirectory(secQuarantine);
                Directory.CreateDirectory(secBackups);

                var secTmpDb = Path.Combine(secQuarantine, $"{backupFileName}.tmp_{Guid.NewGuid():N}");
                var secTmpManifest = $"{secTmpDb}.manifest";
                var secFinalDb = Path.Combine(secBackups, backupFileName);
                var secFinalManifest = Path.Combine(secBackups, $"{backupFileName}.manifest");

                File.Copy(primaryDb, secTmpDb, overwrite: true);
                File.Copy(primaryManifest, secTmpManifest, overwrite: true);

                byte[] secBytes = await File.ReadAllBytesAsync(secTmpDb, ct).ConfigureAwait(false);
                string secSha256 = Convert.ToHexString(SHA256.HashData(secBytes));
                var manifest = await _manifestService.LoadManifestAsync(secTmpManifest, ct).ConfigureAwait(false);

                if (manifest != null && string.Equals(secSha256, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase) &&
                    _manifestService.VerifySignature(manifest, _storeIdentity.GetStoreSigningKey()))
                {
                    File.Move(secTmpDb, secFinalDb, overwrite: true);
                    File.Move(secTmpManifest, secFinalManifest, overwrite: true);

                    await _auditRepo.RecordAsync(
                        nameof(BackupRecord),
                        "BACKUP_SECONDARY_MIRROR_COMPLETED",
                        "System",
                        $"Mirrored backup {backupFileName} to secondary internal storage {settings.SecondaryStoragePath}",
                        ct).ConfigureAwait(false);
                }
                else
                {
                    try { File.Delete(secTmpDb); } catch { }
                    try { File.Delete(secTmpManifest); } catch { }
                }
            }
            catch (Exception ex)
            {
                await _auditRepo.RecordAsync(
                    nameof(BackupRecord),
                    "BACKUP_SECONDARY_STORAGE_UNAVAILABLE",
                    "System",
                    $"Failed to mirror backup to secondary storage: {ex.Message}",
                    ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<DisasterRecoveryExportResultDto> ExportDisasterRecoveryAsync(
        char[] masterRecoveryKeyHex,
        string? destinationPath = null,
        CancellationToken ct = default)
    {
        if (masterRecoveryKeyHex == null || masterRecoveryKeyHex.Length != 64)
        {
            throw new DomainException("StoreMasterRecoveryKey must be a 64-character hexadecimal string.", "INVALID_MASTER_KEY");
        }

        var store = await _dbContext.Stores.AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var storeGuid = store?.StoreGuid ?? _storeIdentity.StoreId;
        var storeName = store?.StoreName ?? _storeIdentity.StoreName;

        if (store != null && !string.IsNullOrWhiteSpace(store.MasterKeyValidatorHash))
        {
            if (!_storeIdentity.ValidateMasterRecoveryKeyAgainstVerifier(storeGuid, masterRecoveryKeyHex, store.MasterKeyValidatorHash))
            {
                throw new DomainException("Invalid StoreMasterRecoveryKey.", "INVALID_MASTER_KEY");
            }
        }
        else if (!_storeIdentity.ValidateMasterRecoveryKey(masterRecoveryKeyHex))
        {
            throw new DomainException("Invalid StoreMasterRecoveryKey.", "INVALID_MASTER_KEY");
        }

        var opId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var quarantineDb = Path.Combine(_quarantineDir, $"quarantine_dr_{opId:N}.tmp");

        var targetFile = !string.IsNullOrWhiteSpace(destinationPath)
            ? destinationPath
            : Path.Combine(RetailPOSPaths.GetDefaultBackupsDirectory(), $"grozet_pos_dr_{timestamp}_{opId:N}.gdrexport");

        try
        {
            var conn = _dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"VACUUM INTO '{quarantineDb.Replace("'", "''")}';";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            ValidateSqliteIntegrity(quarantineDb);

            await PortableDisasterRecoveryExportService.ExportToFileAsync(
                quarantineDb,
                masterRecoveryKeyHex,
                storeGuid,
                storeName,
                targetFile,
                ct).ConfigureAwait(false);

            var fi = new FileInfo(targetFile);
            var header = await PortableDisasterRecoveryExportService.ReadHeaderAsync(targetFile, ct).ConfigureAwait(false);

            await _auditRepo.RecordAsync(
                nameof(BackupRecord),
                "DISASTER_RECOVERY_EXPORT_COMPLETED",
                "Owner",
                $"Exported encrypted disaster recovery package {Path.GetFileName(targetFile)} ({fi.Length} bytes)",
                ct).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            return new DisasterRecoveryExportResultDto
            {
                ExportOperationId = header.ExportOperationId,
                DestinationFilePath = targetFile,
                SizeBytes = fi.Length,
                DatabaseSha256 = header.DatabaseSha256,
                StoreId = storeGuid,
                CreatedAtUtc = header.CreatedAtUtc
            };
        }
        finally
        {
            if (File.Exists(quarantineDb)) try { File.Delete(quarantineDb); } catch { }
        }
    }

    public async Task<BareMetalRecoveryResultDto> ExecuteBareMetalRecoveryAsync(
        string candidatePath,
        char[] masterRecoveryKeyHex,
        string confirmationPhrase,
        CancellationToken ct = default)
    {
        if (confirmationPhrase != BareMetalRecoveryRequestDto.RequiredConfirmationPhrase)
        {
            throw new DomainException($"Confirmation phrase mismatch. Expected '{BareMetalRecoveryRequestDto.RequiredConfirmationPhrase}'.", "CONFIRMATION_PHRASE_MISMATCH");
        }

        if (masterRecoveryKeyHex == null || masterRecoveryKeyHex.Length != 64)
        {
            throw new DomainException("StoreMasterRecoveryKey must be a 64-character hexadecimal string.", "INVALID_MASTER_KEY");
        }

        if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
        {
            throw new FileNotFoundException("Candidate backup file does not exist.", candidatePath);
        }

        using var opLock = await _opCoordinator.AcquireExclusiveLockAsync("BareMetalDisasterRecovery", ct).ConfigureAwait(false);

        var drOpId = Guid.NewGuid();
        var quarantineCandidatePath = Path.Combine(_quarantineDir, $"dr_candidate_{drOpId:N}.tmp");
        var quarantineCandidateDbPath = quarantineCandidatePath;
        var primaryDbPath = Path.Combine(_dataDir, "grozet_pos.db");
        var recoveryStagingDir = Path.Combine(RetailPOSPaths.GetDefaultStagingDirectory(), "Recovery");
        Directory.CreateDirectory(recoveryStagingDir);
        var prevDbPreservedPath = Path.Combine(recoveryStagingDir, $"prev_db_{drOpId:N}.bak");
        var prevIdentityPreservedPath = Path.Combine(recoveryStagingDir, $"prev_identity_{drOpId:N}.bak");
        var securityDir = RetailPOSPaths.GetDefaultSecurityDirectory();
        var primaryIdentityPath = Path.Combine(securityDir, "store_identity.dat");

        byte[]? recoveredSigningKey = null;

        try
        {
            // Stage 1: Candidate file supply into quarantine
            File.Copy(candidatePath, quarantineCandidatePath, overwrite: true);

            // Stage 2: Structural checks
            bool isEncryptedPackage = false;
            using (var s = File.OpenRead(quarantineCandidatePath))
            {
                byte[] magic = new byte[4];
                if (s.Read(magic, 0, 4) == 4 && magic[0] == 0x47 && magic[1] == 0x44 && magic[2] == 0x52 && magic[3] == 0x31)
                {
                    isEncryptedPackage = true;
                }
            }

            if (isEncryptedPackage)
            {
                var decryptedDbPath = Path.Combine(_quarantineDir, $"dr_decrypted_{drOpId:N}.db");
                await PortableDisasterRecoveryExportService.DecryptExportPackageAsync(
                    quarantineCandidatePath,
                    masterRecoveryKeyHex,
                    decryptedDbPath,
                    ct).ConfigureAwait(false);
                quarantineCandidateDbPath = decryptedDbPath;
            }
            else
            {
                if (!VerifySqliteHeader(quarantineCandidateDbPath))
                {
                    throw new DomainException("Candidate file does not have a valid SQLite header.", "INVALID_SQLITE_HEADER");
                }
            }

            // Stage 3 & 4: Phase 16.5 Recovery Key Authentication & Unwrapping
            Guid candidateStoreGuid = Guid.Empty;
            string candidateStoreName = "Restored Store";
            string candidateValidatorHash = string.Empty;
            string candidateWrappedKey = string.Empty;
            int candidateSchemaVersion = 15;

            using (var conn = new SqliteConnection($"Data Source={quarantineCandidateDbPath};Mode=ReadOnly;Pooling=False"))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT store_guid, store_name, master_key_validator_hash, wrapped_store_signing_key FROM stores LIMIT 1;";
                    using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        candidateStoreGuid = reader.GetGuid(0);
                        candidateStoreName = reader.GetString(1);
                        candidateValidatorHash = reader.GetString(2);
                        candidateWrappedKey = reader.GetString(3);
                    }
                    else
                    {
                        throw new DomainException("Candidate database stores table is empty.", "STORE_NOT_FOUND");
                    }
                }

                using (var verCmd = conn.CreateCommand())
                {
                    verCmd.CommandText = "PRAGMA user_version;";
                    var ver = await verCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    if (ver != null) candidateSchemaVersion = Convert.ToInt32(ver, CultureInfo.InvariantCulture);
                }
            }
            SqliteConnection.ClearAllPools();

            // Authenticate Recovery Key against candidate validator using EXACT Phase 16.5 validator
            var valKey = (char[])masterRecoveryKeyHex.Clone();
            bool isValidKey = _storeIdentity.ValidateMasterRecoveryKeyAgainstVerifier(
                candidateStoreGuid,
                valKey,
                candidateValidatorHash);

            if (!isValidKey)
            {
                throw new DomainException("Invalid store master recovery key for candidate database.", "AUTHENTICATION_FAILED_INVALID_RECOVERY_KEY");
            }

            // Stage 4: Unwrap Stable StoreSigningKey using EXACT Phase 16.5 unwrapper
            recoveredSigningKey = _storeIdentity.UnwrapStoreSigningKey(
                candidateStoreGuid,
                masterRecoveryKeyHex,
                candidateWrappedKey);

            // Stage 5: Verify companion manifest if exists
            var companionManifest = $"{candidatePath}.manifest";
            if (File.Exists(companionManifest))
            {
                var manifest = await _manifestService.LoadManifestAsync(companionManifest, ct).ConfigureAwait(false);
                if (manifest != null)
                {
                    if (manifest.StoreId != candidateStoreGuid)
                    {
                        throw new DomainException("Candidate manifest store ID does not match database.", "STORE_ID_MISMATCH");
                    }

                    if (!_manifestService.VerifySignature(manifest, recoveredSigningKey))
                    {
                        throw new DomainException("Candidate manifest signature is invalid.", "MANIFEST_SIGNATURE_INVALID");
                    }
                }
            }

            // Verify SQLite integrity of candidate DB
            ValidateSqliteIntegrity(quarantineCandidateDbPath);

            // Compute candidate SHA-256
            string candidateSha256;
            using (var s = File.OpenRead(quarantineCandidateDbPath))
            {
                var hashBytes = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
                candidateSha256 = Convert.ToHexString(hashBytes).ToUpperInvariant();
            }

            // Stage 6: Crash-Safe Promotion State Machine (7 states)
            // State 1: DR_PREPARED
            string? prevDb = null;
            string? prevIdentity = null;
            if (File.Exists(primaryDbPath))
            {
                File.Copy(primaryDbPath, prevDbPreservedPath, overwrite: true);
                prevDb = prevDbPreservedPath;
            }
            if (File.Exists(primaryIdentityPath))
            {
                File.Copy(primaryIdentityPath, prevIdentityPreservedPath, overwrite: true);
                prevIdentity = prevIdentityPreservedPath;
            }

            await WriteDrMarkerAsync(drOpId, "DR_PREPARED", quarantineCandidateDbPath, candidateSha256, candidateStoreGuid, candidateStoreName, prevDb, prevIdentity, ct).ConfigureAwait(false);

            // State 2: DR_CANDIDATE_VERIFIED
            await WriteDrMarkerAsync(drOpId, "DR_CANDIDATE_VERIFIED", quarantineCandidateDbPath, candidateSha256, candidateStoreGuid, candidateStoreName, prevDb, prevIdentity, ct).ConfigureAwait(false);

            // State 3: DR_PROMOTION_STARTED
            await WriteDrMarkerAsync(drOpId, "DR_PROMOTION_STARTED", quarantineCandidateDbPath, candidateSha256, candidateStoreGuid, candidateStoreName, prevDb, prevIdentity, ct).ConfigureAwait(false);
            SqliteConnection.ClearAllPools();
            var primaryWal = $"{primaryDbPath}-wal";
            var primaryShm = $"{primaryDbPath}-shm";
            if (File.Exists(primaryWal)) try { File.Delete(primaryWal); } catch { }
            if (File.Exists(primaryShm)) try { File.Delete(primaryShm); } catch { }

            // State 4: DR_DB_PROMOTED
            File.Move(quarantineCandidateDbPath, primaryDbPath, overwrite: true);
            await WriteDrMarkerAsync(drOpId, "DR_DB_PROMOTED", primaryDbPath, candidateSha256, candidateStoreGuid, candidateStoreName, prevDb, prevIdentity, ct).ConfigureAwait(false);

            // State 5: DR_IDENTITY_PROMOTED (Clean-Machine DPAPI Reconstruction)
            var initKey = (char[])masterRecoveryKeyHex.Clone();
            await _storeIdentity.InitializeNewStoreIdentityAsync(
                candidateStoreGuid,
                candidateStoreName,
                recoveredSigningKey,
                initKey,
                ct).ConfigureAwait(false);

            // Verify newly generated DPAPI identity on the replacement machine
            await _storeIdentity.ReloadActiveIdentityAsync(ct).ConfigureAwait(false);
            if (_storeIdentity.StoreId != candidateStoreGuid ||
                !CryptographicOperations.FixedTimeEquals(_storeIdentity.GetStoreSigningKey(), recoveredSigningKey))
            {
                throw new DomainException("Reconstructed DPAPI store identity verification failed on replacement machine.", "IDENTITY_VERIFICATION_FAILED");
            }

            await WriteDrMarkerAsync(drOpId, "DR_IDENTITY_PROMOTED", primaryDbPath, candidateSha256, candidateStoreGuid, candidateStoreName, prevDb, prevIdentity, ct).ConfigureAwait(false);

            // State 6: DR_POST_PROMOTION_VERIFYING
            await WriteDrMarkerAsync(drOpId, "DR_POST_PROMOTION_VERIFYING", primaryDbPath, candidateSha256, candidateStoreGuid, candidateStoreName, prevDb, prevIdentity, ct).ConfigureAwait(false);
            ValidateSqliteIntegrity(primaryDbPath);

            // State 7: DR_COMPLETED
            await WriteDrMarkerAsync(drOpId, "DR_COMPLETED", primaryDbPath, candidateSha256, candidateStoreGuid, candidateStoreName, null, null, ct).ConfigureAwait(false);

            // Record audit log
            try
            {
                await _auditRepo.RecordAsync(
                    "Store",
                    "DISASTER_RECOVERY_COMPLETED",
                    "BareMetalOperator",
                    $"Bare-metal recovery completed for store {candidateStoreGuid}. Restored DB and reconstructed local DPAPI identity.",
                    ct).ConfigureAwait(false);
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort audit logging; schema migrations may need to run on candidate DB
            }

            // Clean marker & preserved files
            if (File.Exists(_drRecoveryMarkerPath)) try { File.Delete(_drRecoveryMarkerPath); } catch { }
            if (File.Exists(prevDbPreservedPath)) try { File.Delete(prevDbPreservedPath); } catch { }
            if (File.Exists(prevIdentityPreservedPath)) try { File.Delete(prevIdentityPreservedPath); } catch { }

            return new BareMetalRecoveryResultDto
            {
                OperationId = drOpId,
                Success = true,
                StoreId = candidateStoreGuid,
                StoreName = candidateStoreName,
                RestoredSchemaVersion = candidateSchemaVersion,
                CompletedAtUtc = DateTime.UtcNow,
                Message = "Bare-metal disaster recovery completed successfully. Database and local DPAPI identity promoted."
            };
        }
        catch
        {
            if (File.Exists(quarantineCandidatePath)) try { File.Delete(quarantineCandidatePath); } catch { }
            if (File.Exists(quarantineCandidateDbPath)) try { File.Delete(quarantineCandidateDbPath); } catch { }
            throw;
        }
        finally
        {
            if (recoveredSigningKey != null) CryptographicOperations.ZeroMemory(recoveredSigningKey);
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(masterRecoveryKeyHex.AsSpan()));
        }
    }


    private static void ValidateSqliteIntegrity(string filePath)
    {
        if (!VerifySqliteHeader(filePath))
        {
            throw new DomainException("Invalid SQLite header.", "INVALID_SQLITE_HEADER");
        }

        using (var conn = new SqliteConnection($"Data Source={filePath};Pooling=False"))
        {
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check;";
                var res = (string?)cmd.ExecuteScalar();
                if (!string.Equals(res, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DomainException($"SQLite integrity check failed: {res}", "INTEGRITY_CHECK_FAILED");
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_key_check;";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    throw new DomainException("SQLite foreign key check reported violations.", "FOREIGN_KEY_CHECK_FAILED");
                }
            }
        }
        SqliteConnection.ClearAllPools();
    }

    private static bool VerifySqliteHeader(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            using var s = File.OpenRead(filePath);
            byte[] header = new byte[16];
            int r = s.Read(header, 0, 16);
            if (r < 16) return false;
            var magic = System.Text.Encoding.ASCII.GetString(header);
            return magic.StartsWith("SQLite format 3", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void CheckTargetDiskSpace(string targetDir, long requiredBytes)
    {
        try
        {
            var fullPath = Path.GetFullPath(targetDir);
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                {
                    throw new DomainException(
                        $"Insufficient disk space on drive '{root}'. Required: {requiredBytes} bytes, Available: {drive.AvailableFreeSpace} bytes.",
                        "INSUFFICIENT_DISK_SPACE");
                }
            }
        }
        catch (DomainException)
        {
            throw;
        }
        catch { }
    }

    private async Task WriteMarkerAsync(
        Guid restoreOpId,
        string state,
        string snapshotPath,
        string targetPath,
        string? identitySnapshotPath,
        CancellationToken ct)
    {
        var marker = new
        {
            RestoreOperationId = restoreOpId,
            State = state,
            SafetySnapshotPath = snapshotPath,
            IdentitySafetySnapshotPath = identitySnapshotPath,
            TargetBackupPath = targetPath,
            TimestampUtc = DateTime.UtcNow,
            Mac = string.Empty
        };

        var dir = Path.GetDirectoryName(_recoveryMarkerPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(marker, IndentedJson);
        await File.WriteAllTextAsync(_recoveryMarkerPath, json, ct).ConfigureAwait(false);
    }

    private async Task WriteDrMarkerAsync(
        Guid operationId,
        string state,
        string candidateDbPath,
        string candidateSha256,
        Guid storeId,
        string storeName,
        string? preservedDbPath,
        string? preservedIdentityPath,
        CancellationToken ct)
    {
        var marker = new
        {
            OperationId = operationId,
            State = state,
            CandidateDbPath = candidateDbPath,
            CandidateDbSha256 = candidateSha256,
            StoreId = storeId,
            StoreName = storeName,
            PreviousDbPreservedPath = preservedDbPath,
            PreviousIdentityPreservedPath = preservedIdentityPath,
            CreatedAtUtc = DateTime.UtcNow,
            LastTransitionUtc = DateTime.UtcNow
        };

        var dir = Path.GetDirectoryName(_drRecoveryMarkerPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(marker, IndentedJson);
        await File.WriteAllTextAsync(_drRecoveryMarkerPath, json, ct).ConfigureAwait(false);
    }


    private static BackupRecordDto MapToDto(BackupRecord r)
    {
        return new BackupRecordDto
        {
            BackupOperationId = r.BackupOperationId,
            FileName = r.FileName,
            FilePath = r.FilePath,
            SizeBytes = r.SizeBytes,
            DatabaseSha256 = r.DatabaseSha256,
            Type = r.Type.ToString(),
            Status = r.Status.ToString(),
            FileState = r.FileState.ToString(),
            Origin = r.Origin.ToString(),
            StoreId = r.StoreId,
            StoreName = r.StoreName,
            AppVersion = r.AppVersion,
            SchemaVersion = r.SchemaVersion,
            CreatedAtUtc = r.CreatedAtUtc,
            CompletedAtUtc = r.CompletedAtUtc,
            ErrorMessage = r.ErrorMessage,
            TriggeredByUserId = r.TriggeredByUserId,
            TriggeredByUserName = r.TriggeredByUserName
        };
    }
}
