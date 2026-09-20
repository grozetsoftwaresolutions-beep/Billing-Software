namespace RetailPOS.Application.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Domain.Enums;

public sealed class BackupRetentionService : IBackupRetentionService
{
    private readonly IBackupRepository _backupRepo;
    private readonly IBackupSettingsStore _settingsStore;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStoreIdentityService? _storeIdentityService;
    private readonly IBackupManifestService? _manifestService;

    public BackupRetentionService(
        IBackupRepository backupRepo,
        IBackupSettingsStore settingsStore,
        IUnitOfWork unitOfWork,
        IStoreIdentityService? storeIdentityService = null,
        IBackupManifestService? manifestService = null)
    {
        _backupRepo = backupRepo;
        _settingsStore = settingsStore;
        _unitOfWork = unitOfWork;
        _storeIdentityService = storeIdentityService;
        _manifestService = manifestService;
    }

    public async Task PruneAsync(CancellationToken ct = default)
    {
        var config = await _settingsStore.GetSettingsAsync(ct).ConfigureAwait(false);

        // 1. Identify the protected Last-Known-Good backup that must NEVER be deleted
        var protectedBackupId = await FindProtectedLastKnownGoodBackupIdAsync(ct).ConfigureAwait(false);

        // 2. Count-based pruning per type
        await PruneTypeAsync(BackupType.ScheduledDaily, config.RetentionDailyCount, protectedBackupId, ct).ConfigureAwait(false);
        await PruneTypeAsync(BackupType.ScheduledHourly, config.RetentionHourlyCount, protectedBackupId, ct).ConfigureAwait(false);
        await PruneTypeAsync(BackupType.PreRestoreSnapshot, config.RetentionSnapshotCount, protectedBackupId, ct).ConfigureAwait(false);

        // 3. Storage Ceiling Enforcement
        if (config.MaxStorageCeilingBytes > 0)
        {
            await EnforceStorageCeilingAsync(config.MaxStorageCeilingBytes, protectedBackupId, ct).ConfigureAwait(false);
        }
    }

    private async Task<Guid?> FindProtectedLastKnownGoodBackupIdAsync(CancellationToken ct)
    {
        var allActive = await _backupRepo.GetAllAsync(ct).ConfigureAwait(false);
        var succeeded = allActive
            .Where(r => r.Status == BackupStatus.Succeeded && r.FileState == FileState.Active)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToList();

        foreach (var record in succeeded)
        {
            if (await IsPhysicallyVerifiedAsync(record, ct).ConfigureAwait(false))
            {
                // Return newest physically verified backup
                return record.BackupOperationId;
            }
        }

        // If none strictly verified, fall back to newest existing file
        var existingFileRecord = succeeded.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.FilePath) && File.Exists(r.FilePath));
        return existingFileRecord?.BackupOperationId;
    }

    private async Task<bool> IsPhysicallyVerifiedAsync(BackupRecord record, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(record.FilePath) || !File.Exists(record.FilePath))
        {
            return false;
        }

        var fi = new FileInfo(record.FilePath);
        if (fi.Length == 0)
        {
            return false;
        }

        var manifestPath = $"{record.FilePath}.manifest";
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        if (_manifestService != null && _storeIdentityService != null)
        {
            try
            {
                var manifest = await _manifestService.LoadManifestAsync(manifestPath, ct).ConfigureAwait(false);
                if (manifest == null) return false;

                if (manifest.SizeBytes != fi.Length) return false;

                byte[] fileBytes = await File.ReadAllBytesAsync(record.FilePath, ct).ConfigureAwait(false);
                string sha256 = Convert.ToHexString(SHA256.HashData(fileBytes));
                if (!string.Equals(sha256, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase)) return false;

                byte[] signingKey = _storeIdentityService.GetStoreSigningKey();
                if (!_manifestService.VerifySignature(manifest, signingKey)) return false;

                if (manifest.StoreId != _storeIdentityService.StoreId && _storeIdentityService.StoreId != Guid.Empty)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private async Task PruneTypeAsync(BackupType type, int retainCount, Guid? protectedBackupId, CancellationToken ct)
    {
        var allRecords = await _backupRepo.GetByTypeAsync(type, ct).ConfigureAwait(false);
        var succeeded = allRecords
            .Where(r => r.Status == BackupStatus.Succeeded && r.FileState == FileState.Active)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToList();

        // If retainCount is 0, we still must preserve protected Last-Known-Good!
        int safeRetain = Math.Max(0, retainCount);
        if (succeeded.Count <= safeRetain && safeRetain > 0) return;

        var candidatesToPrune = safeRetain > 0
            ? succeeded.Skip(safeRetain).ToList()
            : succeeded.ToList();

        foreach (var record in candidatesToPrune)
        {
            // SAFETY INVARIANT: Never prune protected Last-Known-Good backup!
            if (protectedBackupId.HasValue && record.BackupOperationId == protectedBackupId.Value)
            {
                continue;
            }

            DeletePhysicalBackupAndArchive(record);
            await _backupRepo.UpdateAsync(record, ct).ConfigureAwait(false);
        }

        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task EnforceStorageCeilingAsync(long maxCeilingBytes, Guid? protectedBackupId, CancellationToken ct)
    {
        var allActive = await _backupRepo.GetAllAsync(ct).ConfigureAwait(false);
        var activeRecords = allActive
            .Where(r => r.Status == BackupStatus.Succeeded && r.FileState == FileState.Active && File.Exists(r.FilePath))
            .OrderBy(r => r.CreatedAtUtc) // oldest first for pruning
            .ToList();

        long currentTotalBytes = activeRecords.Sum(r =>
        {
            try { return new FileInfo(r.FilePath).Length; } catch { return 0L; }
        });

        if (currentTotalBytes <= maxCeilingBytes) return;

        foreach (var record in activeRecords)
        {
            if (currentTotalBytes <= maxCeilingBytes) break;

            // Never delete the only verified recoverable backup!
            if (protectedBackupId.HasValue && record.BackupOperationId == protectedBackupId.Value)
            {
                continue;
            }

            long fileLen = 0;
            try { fileLen = new FileInfo(record.FilePath).Length; } catch { }

            DeletePhysicalBackupAndArchive(record);
            await _backupRepo.UpdateAsync(record, ct).ConfigureAwait(false);

            currentTotalBytes -= fileLen;
        }

        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static void DeletePhysicalBackupAndArchive(BackupRecord record)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(record.FilePath) && File.Exists(record.FilePath))
            {
                File.Delete(record.FilePath);
            }

            var manifestPath = $"{record.FilePath}.manifest";
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }
        }
        catch
        {
            // Non-fatal if filesystem delete encounters transient lock
        }

        record.FileState = FileState.Archived;
    }
}
