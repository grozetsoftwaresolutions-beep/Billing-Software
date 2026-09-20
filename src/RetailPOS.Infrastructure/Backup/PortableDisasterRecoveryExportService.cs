namespace RetailPOS.Infrastructure.Backup;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Domain.Common;

public static class PortableDisasterRecoveryExportService
{
    private static readonly byte[] MagicBytes = new byte[] { 0x47, 0x44, 0x52, 0x31 }; // "GDR1"
    private static readonly byte[] InfoExportKey = Encoding.UTF8.GetBytes("grozet-pos-portable-dr-export-v1");

    public sealed class ExportPackageHeader
    {
        public int FormatVersion { get; set; } = 1;
        public Guid ExportOperationId { get; set; }
        public Guid StoreId { get; set; }
        public string StoreName { get; set; } = string.Empty;
        public string AppVersion { get; set; } = "1.0.0-phase16.7";
        public int SchemaVersion { get; set; } = 15;
        public DateTime CreatedAtUtc { get; set; }
        public string DatabaseSha256 { get; set; } = string.Empty;
        public long PlaintextSizeBytes { get; set; }
    }

    public static byte[] DeriveExportKey(char[] masterRecoveryKeyHex, Guid storeId)
    {
        if (masterRecoveryKeyHex == null || masterRecoveryKeyHex.Length != 64)
        {
            throw new DomainException("StoreMasterRecoveryKey must be 64 hexadecimal characters.", "INVALID_RECOVERY_KEY");
        }

        byte[] masterKeyBytes = Convert.FromHexString(new string(masterRecoveryKeyHex));
        try
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                storeId.ToByteArray(),
                InfoExportKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKeyBytes);
        }
    }

    public static async Task ExportToFileAsync(
        string sourcePlaintextDbPath,
        char[] masterRecoveryKeyHex,
        Guid storeId,
        string storeName,
        string destinationPackagePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourcePlaintextDbPath))
        {
            throw new FileNotFoundException("Source database file not found.", sourcePlaintextDbPath);
        }

        byte[] plaintextBytes = await File.ReadAllBytesAsync(sourcePlaintextDbPath, ct).ConfigureAwait(false);
        string sha256 = Convert.ToHexString(SHA256.HashData(plaintextBytes));
        Guid operationId = Guid.NewGuid();

        var header = new ExportPackageHeader
        {
            FormatVersion = 1,
            ExportOperationId = operationId,
            StoreId = storeId,
            StoreName = storeName,
            AppVersion = "1.0.0-phase16.7",
            SchemaVersion = 15,
            CreatedAtUtc = DateTime.UtcNow,
            DatabaseSha256 = sha256,
            PlaintextSizeBytes = plaintextBytes.Length
        };

        byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
        byte[] exportKey = DeriveExportKey(masterRecoveryKeyHex, storeId);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[plaintextBytes.Length];

        try
        {
            using (var aesGcm = new AesGcm(exportKey, 16))
            {
                aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, headerBytes);
            }

            // Pre-publication verification: test decrypt in-memory before writing
            byte[] testDecrypted = new byte[ciphertext.Length];
            try
            {
                using (var testGcm = new AesGcm(exportKey, 16))
                {
                    testGcm.Decrypt(nonce, ciphertext, tag, testDecrypted, headerBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(testDecrypted);
            }

            // Write container to destination
            var dir = Path.GetDirectoryName(destinationPackagePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmpPath = $"{destinationPackagePath}.tmp_{Guid.NewGuid():N}";
            await using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                await fs.WriteAsync(MagicBytes, ct).ConfigureAwait(false);
                await fs.WriteAsync(BitConverter.GetBytes(headerBytes.Length), ct).ConfigureAwait(false);
                await fs.WriteAsync(headerBytes, ct).ConfigureAwait(false);
                await fs.WriteAsync(nonce, ct).ConfigureAwait(false);
                await fs.WriteAsync(tag, ct).ConfigureAwait(false);
                await fs.WriteAsync(BitConverter.GetBytes((long)ciphertext.Length), ct).ConfigureAwait(false);
                await fs.WriteAsync(ciphertext, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tmpPath, destinationPackagePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exportKey);
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public static async Task<ExportPackageHeader> ReadHeaderAsync(string packagePath, CancellationToken ct = default)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("Disaster recovery package file not found.", packagePath);
        }

        await using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] magic = new byte[4];
        if (await fs.ReadAsync(magic, ct).ConfigureAwait(false) != 4 ||
            !CryptographicOperations.FixedTimeEquals(magic, MagicBytes))
        {
            throw new DomainException("Invalid disaster recovery export package format.", "INVALID_PACKAGE_FORMAT");
        }

        byte[] headerLenBytes = new byte[4];
        await fs.ReadExactlyAsync(headerLenBytes, ct).ConfigureAwait(false);
        int headerLen = BitConverter.ToInt32(headerLenBytes);
        if (headerLen <= 0 || headerLen > 65536)
        {
            throw new DomainException("Corrupted disaster recovery package header length.", "CORRUPT_PACKAGE_HEADER");
        }

        byte[] headerBytes = new byte[headerLen];
        await fs.ReadExactlyAsync(headerBytes, ct).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<ExportPackageHeader>(headerBytes);
        if (header == null)
        {
            throw new DomainException("Failed to deserialize disaster recovery package header.", "CORRUPT_PACKAGE_HEADER");
        }

        return header;
    }

    public static async Task<ExportPackageHeader> DecryptExportPackageAsync(
        string packagePath,
        char[] masterRecoveryKeyHex,
        string targetPlaintextDbPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("Disaster recovery package file not found.", packagePath);
        }

        await using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] magic = new byte[4];
        if (await fs.ReadAsync(magic, ct).ConfigureAwait(false) != 4 ||
            !CryptographicOperations.FixedTimeEquals(magic, MagicBytes))
        {
            throw new DomainException("Invalid disaster recovery export package format.", "INVALID_PACKAGE_FORMAT");
        }

        byte[] headerLenBytes = new byte[4];
        await fs.ReadExactlyAsync(headerLenBytes, ct).ConfigureAwait(false);
        int headerLen = BitConverter.ToInt32(headerLenBytes);
        if (headerLen <= 0 || headerLen > 65536)
        {
            throw new DomainException("Corrupted package header length.", "CORRUPT_PACKAGE_HEADER");
        }

        byte[] headerBytes = new byte[headerLen];
        await fs.ReadExactlyAsync(headerBytes, ct).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<ExportPackageHeader>(headerBytes);
        if (header == null)
        {
            throw new DomainException("Failed to parse package header.", "CORRUPT_PACKAGE_HEADER");
        }

        if (header.SchemaVersion > 15)
        {
            throw new DomainException($"Candidate backup schema version {header.SchemaVersion} exceeds current system supported version 15.", "SCHEMA_VERSION_TOO_NEW");
        }

        byte[] nonce = new byte[12];
        await fs.ReadExactlyAsync(nonce, ct).ConfigureAwait(false);

        byte[] tag = new byte[16];
        await fs.ReadExactlyAsync(tag, ct).ConfigureAwait(false);

        byte[] cipherLenBytes = new byte[8];
        await fs.ReadExactlyAsync(cipherLenBytes, ct).ConfigureAwait(false);
        long cipherLen = BitConverter.ToInt64(cipherLenBytes);

        if (cipherLen <= 0 || cipherLen > 2L * 1024 * 1024 * 1024) // 2GB safety limit
        {
            throw new DomainException("Invalid package ciphertext length.", "INVALID_CIPHERTEXT_LENGTH");
        }

        byte[] ciphertext = new byte[cipherLen];
        await fs.ReadExactlyAsync(ciphertext, ct).ConfigureAwait(false);

        byte[] exportKey = DeriveExportKey(masterRecoveryKeyHex, header.StoreId);
        byte[] plaintextBytes = new byte[cipherLen];

        try
        {
            using (var aesGcm = new AesGcm(exportKey, 16))
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes, headerBytes);
            }

            string actualSha256 = Convert.ToHexString(SHA256.HashData(plaintextBytes));
            if (!string.Equals(actualSha256, header.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException("Decrypted database SHA-256 does not match authenticated header.", "CHECKSUM_MISMATCH");
            }

            var dir = Path.GetDirectoryName(targetPlaintextDbPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(targetPlaintextDbPath, plaintextBytes, ct).ConfigureAwait(false);
            return header;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exportKey);
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }
}
