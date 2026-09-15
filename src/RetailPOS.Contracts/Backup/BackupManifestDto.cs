namespace RetailPOS.Contracts.Backup;

using System;

public class BackupManifestDto
{
    public string ManifestVersion { get; set; } = "1.0";
    public Guid BackupOperationId { get; set; }
    public Guid StoreId { get; set; }
    public string StoreName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DatabaseSha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Type { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string Signature { get; set; } = string.Empty;
}
