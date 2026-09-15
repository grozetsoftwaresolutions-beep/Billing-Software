namespace RetailPOS.Application.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Contracts.Backup;
using RetailPOS.Domain.Enums;

public interface IBackupManifestService
{
    string CreateCanonicalString(BackupManifestDto manifest);
    string ComputeSignature(string canonicalString, byte[] signingKey);
    bool VerifySignature(BackupManifestDto manifest, byte[] signingKey);
    Task<BackupManifestDto> GenerateManifestAsync(string dbFilePath, Guid backupOpId, BackupType type, Guid storeId, string storeName, byte[] signingKey, CancellationToken ct = default);
    Task SaveManifestAsync(string manifestPath, BackupManifestDto manifest, CancellationToken ct = default);
    Task<BackupManifestDto?> LoadManifestAsync(string manifestPath, CancellationToken ct = default);
}
