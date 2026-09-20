namespace RetailPOS.Infrastructure.Backup;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Persistence;

public sealed class FilesystemReconciliationEngine : IFilesystemReconciliationEngine
{
    private readonly IDatabaseOperationGate _gate;
    private readonly IStoreIdentityService _storeIdentity;
    private readonly IBackupManifestService _manifestService;
    private readonly IBackupRepository _backupRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly string _dataDir;
    private readonly string _backupsDir;
    private readonly string _quarantineDir;
    private readonly string _recoveryMarkerPath;
    private readonly string _drRecoveryMarkerPath;

    public FilesystemReconciliationEngine(
        IDatabaseOperationGate gate,
        IStoreIdentityService storeIdentity,
        IBackupManifestService manifestService,
        IBackupRepository backupRepo,
        IUnitOfWork unitOfWork,
        string? customDataDir = null,
        string? customBackupsDir = null,
        string? customRecoveryMarkerPath = null,
        string? customQuarantineDir = null,
        string? customDrRecoveryMarkerPath = null)
    {
        _gate = gate;
        _storeIdentity = storeIdentity;
        _manifestService = manifestService;
        _backupRepo = backupRepo;
        _unitOfWork = unitOfWork;

        _dataDir = customDataDir ?? RetailPOSPaths.GetDefaultDatabaseDirectory();
        _backupsDir = customBackupsDir ?? RetailPOSPaths.GetDefaultBackupsDirectory();
        _quarantineDir = customQuarantineDir ?? Path.Combine(RetailPOSPaths.GetDefaultStagingDirectory(), "Quarantine");
        var recoveryDir = Path.Combine(RetailPOSPaths.GetDefaultStagingDirectory(), "Recovery");
        _recoveryMarkerPath = customRecoveryMarkerPath ?? Path.Combine(recoveryDir, "restore_state.json");
        _drRecoveryMarkerPath = customDrRecoveryMarkerPath ?? Path.Combine(recoveryDir, "dr_recovery_state.json");
    }

    public async Task ReconcileStartupAsync(CancellationToken ct = default)
    {
        // 1. Sweep quarantine leftovers
        SweepQuarantineFiles();

        // 2. Reconcile bare-metal disaster recovery state marker if present
        await ReconcileDisasterRecoveryMarkerAsync(ct).ConfigureAwait(false);

        // 3. Reconcile restore state marker if present
        await ReconcileRestoreMarkerAsync(ct).ConfigureAwait(false);

        // 4. Reconcile two-file publication crash states
        await ReconcilePublicationArtifactsAsync(ct).ConfigureAwait(false);
    }


    private void SweepQuarantineFiles()
    {
        if (Directory.Exists(_quarantineDir))
        {
            try
            {
                var files = Directory.GetFiles(_quarantineDir, "quarantine_*");
                foreach (var file in files)
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }

        if (Directory.Exists(_dataDir))
        {
            try
            {
                var tmpFiles = Directory.GetFiles(_dataDir, "*.tmp_install*");
                foreach (var file in tmpFiles)
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }
    }

    private async Task ReconcileRestoreMarkerAsync(CancellationToken ct)
    {
        if (!File.Exists(_recoveryMarkerPath))
        {
            return;
        }

        RestoreStateMarker? marker = null;
        try
        {
            var json = await File.ReadAllTextAsync(_recoveryMarkerPath, ct).ConfigureAwait(false);
            marker = JsonSerializer.Deserialize<RestoreStateMarker>(json);
        }
        catch
        {
            _gate.TransitionToEmergencyLockdown("Corrupted recovery marker detected on startup.");
            return;
        }

        if (marker == null)
        {
            _gate.TransitionToEmergencyLockdown("Null recovery marker detected on startup.");
            return;
        }

        var primaryDbPath = Path.Combine(_dataDir, "grozet_pos.db");
        var primaryWalPath = $"{primaryDbPath}-wal";
        var primaryShmPath = $"{primaryDbPath}-shm";
        var rollbackDbPath = $"{primaryDbPath}.rollback_staging";
        var rollbackWalPath = $"{primaryDbPath}-wal.rollback_staging";
        var tmpInstallPath = $"{primaryDbPath}.tmp_install";
        var safetySnapshotPath = marker.SafetySnapshotPath;

        switch (marker.State)
        {
            case "Prepared":
                if (File.Exists(safetySnapshotPath)) try { File.Delete(safetySnapshotPath); } catch { }
                DeleteMarker();
                _gate.TransitionToAvailable();
                break;

            case "SafetySnapshotCreated":
                // Row 3: Split DB/WAL move state!
                // Crash after DB move, before WAL move: Rollback DB exists; Primary WAL exists; Primary DB missing
                if (File.Exists(rollbackDbPath) && File.Exists(primaryWalPath) && !File.Exists(primaryDbPath))
                {
                    // Split state! Do NOT guess. Restore verified standalone Safety Snapshot.
                    // Explicitly eliminate orphan Primary WAL, Primary SHM, Rollback DB, and all staging sidecars.
                    if (File.Exists(safetySnapshotPath) && VerifySqliteFile(safetySnapshotPath))
                    {
                        if (File.Exists(primaryWalPath)) try { File.Delete(primaryWalPath); } catch { }
                        if (File.Exists(primaryShmPath)) try { File.Delete(primaryShmPath); } catch { }
                        if (File.Exists(rollbackDbPath)) try { File.Delete(rollbackDbPath); } catch { }
                        if (File.Exists(rollbackWalPath)) try { File.Delete(rollbackWalPath); } catch { }
                        if (File.Exists(tmpInstallPath)) try { File.Delete(tmpInstallPath); } catch { }

                        File.Copy(safetySnapshotPath, primaryDbPath, overwrite: true);
                        ReconcileIdentityRollback(marker);
                        DeleteMarker();
                        _gate.TransitionToAvailable();
                        return;
                    }
                    else
                    {
                        _gate.TransitionToEmergencyLockdown("Row 3 split state detected but safety snapshot is missing or invalid.");
                        return;
                    }
                }

                // Row 4: Paired rollback set exists (Rollback DB & Rollback WAL exist, Primary DB missing)
                if (File.Exists(rollbackDbPath) && File.Exists(rollbackWalPath) && !File.Exists(primaryDbPath))
                {
                    File.Move(rollbackDbPath, primaryDbPath, overwrite: true);
                    File.Move(rollbackWalPath, primaryWalPath, overwrite: true);
                    ReconcileIdentityRollback(marker);
                    DeleteMarker();
                    _gate.TransitionToAvailable();
                    return;
                }

                // Normal SafetySnapshotCreated: Active DB intact; Safety Snapshot verified
                if (File.Exists(primaryDbPath))
                {
                    DeleteMarker();
                    _gate.TransitionToAvailable();
                    return;
                }
                break;

            case "ProductionDbStaged":
            case "TargetInstalled":
                if (File.Exists(rollbackDbPath))
                {
                    if (File.Exists(primaryDbPath)) try { File.Delete(primaryDbPath); } catch { }
                    if (File.Exists(primaryWalPath)) try { File.Delete(primaryWalPath); } catch { }
                    if (File.Exists(primaryShmPath)) try { File.Delete(primaryShmPath); } catch { }
                    if (File.Exists(tmpInstallPath)) try { File.Delete(tmpInstallPath); } catch { }

                    File.Move(rollbackDbPath, primaryDbPath, overwrite: true);
                    if (File.Exists(rollbackWalPath))
                    {
                        File.Move(rollbackWalPath, primaryWalPath, overwrite: true);
                    }
                    ReconcileIdentityRollback(marker);
                    DeleteMarker();
                    _gate.TransitionToAvailable();
                    return;
                }
                else if (File.Exists(safetySnapshotPath) && VerifySqliteFile(safetySnapshotPath))
                {
                    File.Copy(safetySnapshotPath, primaryDbPath, overwrite: true);
                    ReconcileIdentityRollback(marker);
                    DeleteMarker();
                    _gate.TransitionToAvailable();
                    return;
                }
                break;

            case "MigrationStarted":
            case "MigrationCompleted":
            case "VerificationStarted":
                if (File.Exists(safetySnapshotPath) && VerifySqliteFile(safetySnapshotPath))
                {
                    if (File.Exists(primaryDbPath)) try { File.Delete(primaryDbPath); } catch { }
                    if (File.Exists(primaryWalPath)) try { File.Delete(primaryWalPath); } catch { }
                    if (File.Exists(primaryShmPath)) try { File.Delete(primaryShmPath); } catch { }
                    if (File.Exists(rollbackDbPath)) try { File.Delete(rollbackDbPath); } catch { }
                    if (File.Exists(rollbackWalPath)) try { File.Delete(rollbackWalPath); } catch { }
                    if (File.Exists(tmpInstallPath)) try { File.Delete(tmpInstallPath); } catch { }

                    File.Copy(safetySnapshotPath, primaryDbPath, overwrite: true);
                    ReconcileIdentityRollback(marker);
                    DeleteMarker();
                    _gate.TransitionToAvailable();
                    return;
                }
                else
                {
                    _gate.TransitionToEmergencyLockdown($"Restoration interrupted at {marker.State} and safety snapshot is unreadable.");
                    return;
                }

            case "VerificationCompleted":
            case "Finalized":
                if (File.Exists(rollbackDbPath)) try { File.Delete(rollbackDbPath); } catch { }
                if (File.Exists(rollbackWalPath)) try { File.Delete(rollbackWalPath); } catch { }
                if (File.Exists(tmpInstallPath)) try { File.Delete(tmpInstallPath); } catch { }
                var idRollback = Path.Combine(RetailPOSPaths.GetDefaultSecurityDirectory(), "store_identity.dat.rollback_staging");
                if (File.Exists(idRollback)) try { File.Delete(idRollback); } catch { }

                DeleteMarker();
                _gate.TransitionToAvailable();
                return;

        }

        _gate.TransitionToEmergencyLockdown("Ambiguous recovery marker and physical file state on startup.");
    }

    private void DeleteMarker()
    {
        try
        {
            if (File.Exists(_recoveryMarkerPath))
            {
                File.Delete(_recoveryMarkerPath);
            }
        }
        catch { }
    }

    private void ReconcileIdentityRollback(RestoreStateMarker marker)
    {
        var identityRollbackPath = Path.Combine(RetailPOSPaths.GetDefaultSecurityDirectory(), "store_identity.dat.rollback_staging");
        if (File.Exists(identityRollbackPath))
        {
            _storeIdentity.RestoreIdentitySafetySnapshot(identityRollbackPath);
            try { File.Delete(identityRollbackPath); } catch { }
        }
        else if (!string.IsNullOrWhiteSpace(marker.IdentitySafetySnapshotPath) && File.Exists(marker.IdentitySafetySnapshotPath))
        {
            _storeIdentity.RestoreIdentitySafetySnapshot(marker.IdentitySafetySnapshotPath);
        }
    }

    private async Task ReconcilePublicationArtifactsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_backupsDir)) return;

        var signingKey = _storeIdentity.GetStoreSigningKey();
        var dbFiles = Directory.GetFiles(_backupsDir, "*.db");

        foreach (var dbFile in dbFiles)
        {
            var fileName = Path.GetFileName(dbFile);
            if (fileName.StartsWith("grozet_pos_safety_snapshot_", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("safety_snapshot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var manifestPath = $"{dbFile}.manifest";

            if (!File.Exists(manifestPath))
            {
                try { File.Delete(dbFile); } catch { }
                var record = await _backupRepo.GetByFileNameAsync(fileName, ct).ConfigureAwait(false);
                if (record != null && record.Status != BackupStatus.Failed)
                {
                    record.Status = BackupStatus.Failed;
                    record.FileState = FileState.Missing;
                    record.ErrorMessage = "Orphan database file without companion manifest.";
                    await _backupRepo.UpdateAsync(record, ct).ConfigureAwait(false);
                }
                continue;
            }

            var manifest = await _manifestService.LoadManifestAsync(manifestPath, ct).ConfigureAwait(false);
            if (manifest == null || !_manifestService.VerifySignature(manifest, signingKey) || !VerifySqliteFile(dbFile))
            {
                try { File.Delete(dbFile); } catch { }
                try { File.Delete(manifestPath); } catch { }
                continue;
            }

            var existingRecord = await _backupRepo.GetByFileNameAsync(fileName, ct).ConfigureAwait(false);
            if (existingRecord == null)
            {
                var newRecord = new BackupRecord
                {
                    BackupOperationId = manifest.BackupOperationId != Guid.Empty ? manifest.BackupOperationId : Guid.NewGuid(),
                    FileName = fileName,
                    FilePath = dbFile,
                    SizeBytes = manifest.SizeBytes,
                    DatabaseSha256 = manifest.DatabaseSha256,
                    Type = Enum.TryParse<BackupType>(manifest.Type, out var parsedType) ? parsedType : BackupType.Manual,
                    Status = BackupStatus.Succeeded,
                    FileState = FileState.Active,
                    Origin = BackupOrigin.LocalGenerated,
                    StoreId = manifest.StoreId,
                    StoreName = manifest.StoreName,
                    AppVersion = manifest.AppVersion,
                    SchemaVersion = manifest.SchemaVersion,
                    CreatedAtUtc = manifest.CreatedAtUtc,
                    CompletedAtUtc = DateTime.UtcNow,
                    TriggeredByUserId = "SYSTEM_RECONCILIATION",
                    TriggeredByUserName = "System Startup Reconciliation"
                };
                await _backupRepo.InsertAsync(newRecord, ct).ConfigureAwait(false);
            }
        }

        var manifestFiles = Directory.GetFiles(_backupsDir, "*.manifest");
        foreach (var manifestFile in manifestFiles)
        {
            var dbFile = manifestFile.Substring(0, manifestFile.Length - ".manifest".Length);
            if (!File.Exists(dbFile))
            {
                try { File.Delete(manifestFile); } catch { }
            }
        }

        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static bool VerifySqliteFile(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            using var stream = File.OpenRead(filePath);
            byte[] header = new byte[16];
            int read = stream.Read(header, 0, 16);
            if (read < 16) return false;
            var magic = System.Text.Encoding.ASCII.GetString(header);
            return magic.StartsWith("SQLite format 3", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task ReconcileDisasterRecoveryMarkerAsync(CancellationToken ct)
    {
        if (!File.Exists(_drRecoveryMarkerPath))
        {
            return;
        }

        DisasterRecoveryStateMarker? marker = null;
        try
        {
            var json = await File.ReadAllTextAsync(_drRecoveryMarkerPath, ct).ConfigureAwait(false);
            marker = JsonSerializer.Deserialize<DisasterRecoveryStateMarker>(json);
        }
        catch
        {
            _gate.TransitionToEmergencyLockdown("Corrupted bare-metal disaster recovery marker detected on startup.");
            return;
        }

        if (marker == null)
        {
            _gate.TransitionToEmergencyLockdown("Null bare-metal disaster recovery marker detected on startup.");
            return;
        }

        var primaryDbPath = Path.Combine(_dataDir, "grozet_pos.db");
        var securityDir = RetailPOSPaths.GetDefaultSecurityDirectory();
        var primaryIdentityPath = Path.Combine(securityDir, "store_identity.dat");

        switch (marker.State)
        {
            case "DR_PREPARED":
            case "DR_CANDIDATE_VERIFIED":
                if (!string.IsNullOrWhiteSpace(marker.PreviousDbPreservedPath) && File.Exists(marker.PreviousDbPreservedPath))
                {
                    try { File.Delete(marker.PreviousDbPreservedPath); } catch { }
                }
                if (!string.IsNullOrWhiteSpace(marker.PreviousIdentityPreservedPath) && File.Exists(marker.PreviousIdentityPreservedPath))
                {
                    try { File.Delete(marker.PreviousIdentityPreservedPath); } catch { }
                }
                DeleteDrMarker();
                break;

            case "DR_PROMOTION_STARTED":
                if (!string.IsNullOrWhiteSpace(marker.PreviousDbPreservedPath) && File.Exists(marker.PreviousDbPreservedPath))
                {
                    File.Copy(marker.PreviousDbPreservedPath, primaryDbPath, overwrite: true);
                    if (!string.IsNullOrWhiteSpace(marker.PreviousIdentityPreservedPath) && File.Exists(marker.PreviousIdentityPreservedPath))
                    {
                        File.Copy(marker.PreviousIdentityPreservedPath, primaryIdentityPath, overwrite: true);
                    }
                    DeleteDrMarker();
                    _gate.TransitionToAvailable();
                }
                else
                {
                    if (File.Exists(primaryDbPath)) try { File.Delete(primaryDbPath); } catch { }
                    _gate.TransitionToEmergencyLockdown("Bare-metal recovery interrupted during DB promotion. Re-run disaster recovery.");
                }
                break;

            case "DR_DB_PROMOTED":
                if (!string.IsNullOrWhiteSpace(marker.PreviousDbPreservedPath) && File.Exists(marker.PreviousDbPreservedPath))
                {
                    File.Copy(marker.PreviousDbPreservedPath, primaryDbPath, overwrite: true);
                    if (!string.IsNullOrWhiteSpace(marker.PreviousIdentityPreservedPath) && File.Exists(marker.PreviousIdentityPreservedPath))
                    {
                        File.Copy(marker.PreviousIdentityPreservedPath, primaryIdentityPath, overwrite: true);
                    }
                    DeleteDrMarker();
                    _gate.TransitionToAvailable();
                }
                else
                {
                    _gate.TransitionToEmergencyLockdown("Bare-metal recovery interrupted during identity reconstruction. Re-run 'retailpos-server.exe --disaster-recovery' with StoreMasterRecoveryKey.");
                }
                break;

            case "DR_IDENTITY_PROMOTED":
            case "DR_POST_PROMOTION_VERIFYING":
                if (File.Exists(primaryDbPath) && VerifySqliteFile(primaryDbPath) && File.Exists(primaryIdentityPath))
                {
                    try
                    {
                        await _storeIdentity.ReloadActiveIdentityAsync(ct).ConfigureAwait(false);
                        if (_storeIdentity.StoreId == marker.StoreId)
                        {
                            if (!string.IsNullOrWhiteSpace(marker.PreviousDbPreservedPath) && File.Exists(marker.PreviousDbPreservedPath))
                            {
                                try { File.Delete(marker.PreviousDbPreservedPath); } catch { }
                            }
                            if (!string.IsNullOrWhiteSpace(marker.PreviousIdentityPreservedPath) && File.Exists(marker.PreviousIdentityPreservedPath))
                            {
                                try { File.Delete(marker.PreviousIdentityPreservedPath); } catch { }
                            }
                            DeleteDrMarker();
                            _gate.TransitionToAvailable();
                            return;
                        }
                    }
                    catch { }
                }
                _gate.TransitionToEmergencyLockdown("Bare-metal post-promotion verification failed during startup reconciliation.");
                break;

            case "DR_COMPLETED":
                DeleteDrMarker();
                _gate.TransitionToAvailable();
                break;

            default:
                _gate.TransitionToEmergencyLockdown($"Unknown bare-metal disaster recovery marker state '{marker.State}'.");
                break;
        }
    }

    private void DeleteDrMarker()
    {
        if (File.Exists(_drRecoveryMarkerPath))
        {
            try { File.Delete(_drRecoveryMarkerPath); } catch { }
        }
    }

    public sealed class RestoreStateMarker
    {
        public Guid RestoreOperationId { get; set; }
        public string State { get; set; } = string.Empty;
        public string SafetySnapshotPath { get; set; } = string.Empty;
        public string? IdentitySafetySnapshotPath { get; set; }
        public string TargetBackupPath { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
        public string Mac { get; set; } = string.Empty;
    }

    public sealed class DisasterRecoveryStateMarker
    {
        public Guid OperationId { get; set; }
        public string State { get; set; } = string.Empty;
        public string CandidateDbPath { get; set; } = string.Empty;
        public string CandidateDbSha256 { get; set; } = string.Empty;
        public Guid StoreId { get; set; }
        public string StoreName { get; set; } = string.Empty;
        public string? PreviousDbPreservedPath { get; set; }
        public string? PreviousIdentityPreservedPath { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime LastTransitionUtc { get; set; }
    }
}

