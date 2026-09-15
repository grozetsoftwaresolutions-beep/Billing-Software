namespace RetailPOS.Application.Services;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Enums;

public sealed class BackupRetentionService : IBackupRetentionService
{
    private readonly IBackupRepository _backupRepo;
    private readonly IBackupSettingsStore _settingsStore;
    private readonly IUnitOfWork _unitOfWork;

    public BackupRetentionService(
        IBackupRepository backupRepo,
        IBackupSettingsStore settingsStore,
        IUnitOfWork unitOfWork)
    {
        _backupRepo = backupRepo;
        _settingsStore = settingsStore;
        _unitOfWork = unitOfWork;
    }

    public async Task PruneAsync(CancellationToken ct = default)
    {
        var config = await _settingsStore.GetSettingsAsync(ct).ConfigureAwait(false);

        await PruneTypeAsync(BackupType.ScheduledDaily, config.RetentionDailyCount, ct).ConfigureAwait(false);
        await PruneTypeAsync(BackupType.ScheduledHourly, config.RetentionHourlyCount, ct).ConfigureAwait(false);
        await PruneTypeAsync(BackupType.PreRestoreSnapshot, config.RetentionSnapshotCount, ct).ConfigureAwait(false);
    }

    private async Task PruneTypeAsync(BackupType type, int retainCount, CancellationToken ct)
    {
        if (retainCount <= 0) return;

        var allRecords = await _backupRepo.GetByTypeAsync(type, ct).ConfigureAwait(false);
        var succeeded = allRecords
            .Where(r => r.Status == BackupStatus.Succeeded && r.FileState == FileState.Active)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToList();

        if (succeeded.Count <= retainCount) return;

        var toPrune = succeeded.Skip(retainCount).ToList();
        foreach (var record in toPrune)
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
                // Continue pruning even if physical file delete encounters non-fatal issue
            }

            record.FileState = FileState.Archived;
            await _backupRepo.UpdateAsync(record, ct).ConfigureAwait(false);
        }

        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
