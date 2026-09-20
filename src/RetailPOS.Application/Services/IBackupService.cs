namespace RetailPOS.Application.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Contracts.Backup;
using RetailPOS.Domain.Enums;

public interface IBackupService
{
    Task<BackupRecordDto> CreateBackupAsync(BackupType type, string triggeredByUserId, string triggeredByUserName, string? reason = null, CancellationToken ct = default);
    Task<IReadOnlyList<BackupRecordDto>> GetBackupHistoryAsync(CancellationToken ct = default);
    Task<BackupRecordDto?> GetBackupByIdAsync(Guid operationId, CancellationToken ct = default);
    Task<BackupIntegrityReportDto> VerifyBackupAsync(string fileName, CancellationToken ct = default);
    Task<BackupRecordDto> IngestExternalBackupAsync(Stream dbStream, string dbFileName, Stream manifestStream, string manifestFileName, string userId, string userName, CancellationToken ct = default);
    Task<BackupRecordDto> RestoreBackupAsync(string fileName, string triggeredByUserId, string triggeredByUserName, CancellationToken ct = default);
    Task EmergencyRestoreAsync(string fileName, char[] masterRecoveryKeyHex, CancellationToken ct = default);
    Task<BackupSettingsDto> GetSettingsAsync(CancellationToken ct = default);
    Task<BackupSettingsDto> UpdateSettingsAsync(UpdateBackupSettingsDto updateDto, CancellationToken ct = default);
    Task<DisasterRecoveryExportResultDto> ExportDisasterRecoveryAsync(char[] masterRecoveryKeyHex, string? destinationPath = null, CancellationToken ct = default);
    Task<BareMetalRecoveryResultDto> ExecuteBareMetalRecoveryAsync(string candidatePath, char[] masterRecoveryKeyHex, string confirmationPhrase, CancellationToken ct = default);
    Task MirrorBackupToSecondaryAsync(string backupFileName, CancellationToken ct = default);
}

