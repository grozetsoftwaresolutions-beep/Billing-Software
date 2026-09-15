namespace RetailPOS.Domain.Entities.Backup;

using System;
using RetailPOS.Domain.Enums;

public class BackupRecord
{
    public Guid BackupOperationId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string DatabaseSha256 { get; set; } = string.Empty;
    public BackupType Type { get; set; }
    public BackupStatus Status { get; set; }
    public FileState FileState { get; set; }
    public BackupOrigin Origin { get; set; }
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
