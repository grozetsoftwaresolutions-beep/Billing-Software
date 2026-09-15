namespace RetailPOS.Infrastructure.Backup;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;

public sealed class BackupManifestService : IBackupManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string CreateCanonicalString(BackupManifestDto manifest)
    {
        return $"ManifestVersion={manifest.ManifestVersion}\n" +
               $"BackupOperationId={manifest.BackupOperationId}\n" +
               $"StoreId={manifest.StoreId}\n" +
               $"StoreName={manifest.StoreName}\n" +
               $"FileName={manifest.FileName}\n" +
               $"DatabaseSha256={manifest.DatabaseSha256}\n" +
               $"SizeBytes={manifest.SizeBytes}\n" +
               $"Type={manifest.Type}\n" +
               $"CreatedAtUtc={manifest.CreatedAtUtc:O}\n" +
               $"AppVersion={manifest.AppVersion}\n" +
               $"SchemaVersion={manifest.SchemaVersion}";
    }

    public string ComputeSignature(string canonicalString, byte[] signingKey)
    {
        using var hmac = new HMACSHA256(signingKey);
        var bytes = Encoding.UTF8.GetBytes(canonicalString);
        var hash = hmac.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    public bool VerifySignature(BackupManifestDto manifest, byte[] signingKey)
    {
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Signature) || signingKey == null || signingKey.Length == 0)
        {
            return false;
        }

        var canonical = CreateCanonicalString(manifest);
        var expectedSig = ComputeSignature(canonical, signingKey);

        var actualBytes = Encoding.UTF8.GetBytes(manifest.Signature.ToUpperInvariant());
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSig.ToUpperInvariant());

        if (actualBytes.Length != expectedBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    public async Task<BackupManifestDto> GenerateManifestAsync(
        string dbFilePath,
        Guid backupOpId,
        BackupType type,
        Guid storeId,
        string storeName,
        byte[] signingKey,
        CancellationToken ct = default)
    {
        if (!File.Exists(dbFilePath))
        {
            throw new FileNotFoundException($"Database file not found: {dbFilePath}", dbFilePath);
        }

        var fi = new FileInfo(dbFilePath);
        var sizeBytes = fi.Length;

        string sha256;
        using (var stream = File.OpenRead(dbFilePath))
        {
            var hashBytes = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            sha256 = Convert.ToHexString(hashBytes).ToUpperInvariant();
        }

        var manifest = new BackupManifestDto
        {
            ManifestVersion = "1.0",
            BackupOperationId = backupOpId,
            StoreId = storeId,
            StoreName = storeName,
            FileName = Path.GetFileName(dbFilePath),
            DatabaseSha256 = sha256,
            SizeBytes = sizeBytes,
            Type = type.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
            AppVersion = "1.0.0",
            SchemaVersion = 11
        };

        var canonical = CreateCanonicalString(manifest);
        manifest.Signature = ComputeSignature(canonical, signingKey);
        return manifest;
    }

    public async Task SaveManifestAsync(string manifestPath, BackupManifestDto manifest, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var dir = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(manifestPath, json, ct).ConfigureAwait(false);
    }

    public async Task<BackupManifestDto?> LoadManifestAsync(string manifestPath, CancellationToken ct = default)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<BackupManifestDto>(json);
        }
        catch
        {
            return null;
        }
    }
}
