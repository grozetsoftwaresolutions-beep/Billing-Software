namespace RetailPOS.Infrastructure.Tests;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using RetailPOS.Domain.Common;
using RetailPOS.Infrastructure.Backup;
using Xunit;

public sealed class PortableDisasterRecoveryExportTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _sampleDbPath;
    private readonly char[] _sampleMasterKey;
    private readonly Guid _sampleStoreId;

    public PortableDisasterRecoveryExportTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "RetailPOS_DrExportTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _sampleStoreId = Guid.NewGuid();
        _sampleMasterKey = "a1b2c3d4e5f60718293a4b5c6d7e8f90123456789abcdef0123456789abcdef0".ToCharArray();

        // Create a realistic sample SQLite database file with valid header
        _sampleDbPath = Path.Combine(_testDir, "sample_backup.db");
        using (var fs = File.Create(_sampleDbPath))
        {
            var header = Encoding.ASCII.GetBytes("SQLite format 3\0");
            fs.Write(header, 0, header.Length);
            var dummyData = new byte[4096 - header.Length];
            RandomNumberGenerator.Fill(dummyData);
            fs.Write(dummyData, 0, dummyData.Length);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task Export_CreatesEncryptedGdrPackage_WithMagicBytesAndHeader()
    {
        var packagePath = Path.Combine(_testDir, "test_export.gdr");

        await PortableDisasterRecoveryExportService.ExportToFileAsync(
            _sampleDbPath,
            _sampleMasterKey,
            _sampleStoreId,
            "Test Store",
            packagePath);

        Assert.True(File.Exists(packagePath));
        var header = await PortableDisasterRecoveryExportService.ReadHeaderAsync(packagePath);
        Assert.NotNull(header);
        Assert.Equal(_sampleStoreId, header.StoreId);
        Assert.Equal(15, header.SchemaVersion);

        // Check file header
        using var fs = File.OpenRead(packagePath);
        byte[] magic = new byte[4];
        fs.ReadExactly(magic, 0, 4);
        Assert.Equal(new byte[] { 0x47, 0x44, 0x52, 0x31 }, magic); // "GDR1"
    }

    [Fact]
    public async Task Export_RoundTrip_TestDecryptionSucceedsAndMatchesSourceDb()
    {
        var packagePath = Path.Combine(_testDir, "test_roundtrip.gdr");
        var decryptedDbPath = Path.Combine(_testDir, "decrypted_output.db");

        await PortableDisasterRecoveryExportService.ExportToFileAsync(
            _sampleDbPath,
            _sampleMasterKey,
            _sampleStoreId,
            "Test Store",
            packagePath);

        var header = await PortableDisasterRecoveryExportService.DecryptExportPackageAsync(
            packagePath,
            _sampleMasterKey,
            decryptedDbPath);

        Assert.NotNull(header);
        Assert.Equal(_sampleStoreId, header.StoreId);
        Assert.Equal(15, header.SchemaVersion);

        // Verify SHA-256 of decrypted file exactly matches source
        var origHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_sampleDbPath)));
        var decHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(decryptedDbPath)));

        Assert.Equal(origHash, decHash);
    }

    [Fact]
    public async Task Export_WrongRecoveryKey_ThrowsAuthenticationException()
    {
        var packagePath = Path.Combine(_testDir, "test_wrong_key.gdr");
        var decryptedDbPath = Path.Combine(_testDir, "decrypted_wrong.db");

        await PortableDisasterRecoveryExportService.ExportToFileAsync(
            _sampleDbPath,
            _sampleMasterKey,
            _sampleStoreId,
            "Test Store",
            packagePath);

        var wrongKey = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff".ToCharArray();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            PortableDisasterRecoveryExportService.DecryptExportPackageAsync(
                packagePath,
                wrongKey,
                decryptedDbPath));

        // Decrypted target file must NOT exist if decryption fails
        Assert.False(File.Exists(decryptedDbPath));
    }

    [Fact]
    public async Task Export_TamperedCiphertext_ThrowsCryptographicException()
    {
        var packagePath = Path.Combine(_testDir, "test_tampered.gdr");
        var decryptedDbPath = Path.Combine(_testDir, "decrypted_tampered.db");

        await PortableDisasterRecoveryExportService.ExportToFileAsync(
            _sampleDbPath,
            _sampleMasterKey,
            _sampleStoreId,
            "Test Store",
            packagePath);

        // Tamper with the package by flipping bits at the end of the file (tag / ciphertext)
        var bytes = await File.ReadAllBytesAsync(packagePath);
        bytes[^5] ^= 0xFF; // Flip bits near end
        await File.WriteAllBytesAsync(packagePath, bytes);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            PortableDisasterRecoveryExportService.DecryptExportPackageAsync(
                packagePath,
                _sampleMasterKey,
                decryptedDbPath));

        Assert.False(File.Exists(decryptedDbPath));
    }

    [Fact]
    public async Task Export_TamperedAad_ThrowsCryptographicException()
    {
        var packagePath = Path.Combine(_testDir, "test_tampered_aad.gdr");
        var decryptedDbPath = Path.Combine(_testDir, "decrypted_tampered_aad.db");

        await PortableDisasterRecoveryExportService.ExportToFileAsync(
            _sampleDbPath,
            _sampleMasterKey,
            _sampleStoreId,
            "Test Store",
            packagePath);

        // Header JSON is in the plaintext prefix (after magic bytes, length, salt, nonce)
        // Tampering with the JSON header must cause GCM tag validation to fail because it is included in AAD
        var bytes = await File.ReadAllBytesAsync(packagePath);
        // Find "schemaVersion" in the file and modify it
        var jsonStr = Encoding.UTF8.GetString(bytes);
        int idx = jsonStr.IndexOf("\"schemaVersion\":15", StringComparison.Ordinal);
        if (idx >= 0)
        {
            bytes[idx + 16] = (byte)'6'; // Change 15 to 16
            await File.WriteAllBytesAsync(packagePath, bytes);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                PortableDisasterRecoveryExportService.DecryptExportPackageAsync(
                    packagePath,
                    _sampleMasterKey,
                    decryptedDbPath));

            Assert.False(File.Exists(decryptedDbPath));
        }
    }
}
