namespace RetailPOS.Contracts.Backup;

using System;

public class CreateBackupRequestDto
{
    public string? Reason { get; set; }
}

public class RestoreBackupRequestDto
{
    public string FileName { get; set; } = string.Empty;
}

public class EmergencyRestoreRequestDto
{
    public const string RequiredConfirmationPhrase = "CONFIRM_DESTRUCTIVE_ROLLBACK";

    public string FileName { get; set; } = string.Empty;
    public string StoreMasterRecoveryKey { get; set; } = string.Empty;
    public bool ConfirmDestructiveRollback { get; set; } = true;
    public string ConfirmationPhrase { get; set; } = string.Empty;
}

public class BackupRecordDto
{
    public Guid BackupOperationId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string DatabaseSha256 { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string FileState { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public Guid StoreId { get; set; }
    public string StoreName { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public string TriggeredByUserId { get; set; } = string.Empty;
    public string TriggeredByUserName { get; set; } = string.Empty;
}

public class BackupSettingsDto
{
    public bool DailyBackupEnabled { get; set; } = true;
    public string DailyBackupTime { get; set; } = "23:00";
    public bool HourlyBackupEnabled { get; set; }
    public int HourlyIntervalHours { get; set; } = 4;
    public int RetentionDailyCount { get; set; } = 14;
    public int RetentionHourlyCount { get; set; } = 48;
    public int RetentionSnapshotCount { get; set; } = 5;
    public string BackupStoragePath { get; set; } = string.Empty;
    public string? SecondaryStoragePath { get; set; }
    public string? RemovableUsbBackupPath { get; set; }
    public long MaxStorageCeilingBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB default
}

public class UpdateBackupSettingsDto
{
    public bool DailyBackupEnabled { get; set; }
    public string DailyBackupTime { get; set; } = "23:00";
    public bool HourlyBackupEnabled { get; set; }
    public int HourlyIntervalHours { get; set; }
    public int RetentionDailyCount { get; set; }
    public int RetentionHourlyCount { get; set; }
    public int RetentionSnapshotCount { get; set; }
    public string? BackupStoragePath { get; set; }
    public string? SecondaryStoragePath { get; set; }
    public string? RemovableUsbBackupPath { get; set; }
    public long? MaxStorageCeilingBytes { get; set; }
}

public class BackupProgressDto
{
    public Guid OperationId { get; set; }
    public string State { get; set; } = string.Empty;
    public int ProgressPercentage { get; set; }
    public string? Message { get; set; }
}

public class BackupIntegrityReportDto
{
    public string FileName { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public bool ManifestValid { get; set; }
    public bool ChecksumValid { get; set; }
    public bool SqliteIntegrityValid { get; set; }
    public bool ForeignKeyCheckValid { get; set; }
    public string? Details { get; set; }
}

public class ExportDisasterRecoveryRequestDto
{
    public string StoreMasterRecoveryKey { get; set; } = string.Empty;
    public string? DestinationPath { get; set; }
}

public class DisasterRecoveryExportResultDto
{
    public Guid ExportOperationId { get; set; }
    public string DestinationFilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string DatabaseSha256 { get; set; } = string.Empty;
    public Guid StoreId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class BareMetalRecoveryRequestDto
{
    public const string RequiredConfirmationPhrase = "CONFIRM_BARE_METAL_RECOVERY";

    public string SourceBackupPath { get; set; } = string.Empty;
    public string StoreMasterRecoveryKey { get; set; } = string.Empty;
    public string ConfirmationPhrase { get; set; } = string.Empty;
}

public class BareMetalRecoveryResultDto
{
    public Guid OperationId { get; set; }
    public bool Success { get; set; }
    public Guid StoreId { get; set; }
    public string StoreName { get; set; } = string.Empty;
    public int RestoredSchemaVersion { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public string Message { get; set; } = string.Empty;
}

