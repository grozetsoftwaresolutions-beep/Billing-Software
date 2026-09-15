namespace RetailPOS.Infrastructure.Security;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Application.Services;
using RetailPOS.Domain.Common;

public sealed class StoreIdentityService : IStoreIdentityService
{
    private readonly string _securityFilePath;
    private readonly object _lock = new();
    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private Guid _storeId;
    private string _storeName = string.Empty;
    private Guid _installationId;
    private byte[] _storeSigningKey = Array.Empty<byte>();
    private byte[] _installationSecret = Array.Empty<byte>();
    private byte[] _masterKeyValidatorHash = Array.Empty<byte>();

    public Guid StoreId => _storeId;
    public string StoreName => _storeName;

    public StoreIdentityService(string? customSecurityDir = null)
    {
        var baseDir = customSecurityDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrozetPOS", "Security");
        _securityFilePath = Path.Combine(baseDir, "store_identity.dat");
    }

    public async Task InitializeOrLoadAsync(Guid storeId, string storeName, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _storeName = storeName;
        }

        if (File.Exists(_securityFilePath))
        {
            try
            {
                var encryptedBytes = await File.ReadAllBytesAsync(_securityFilePath, ct).ConfigureAwait(false);
                byte[] decryptedBytes;
                if (OperatingSystem.IsWindows())
                {
                    decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.LocalMachine);
                }
                else
                {
                    decryptedBytes = encryptedBytes;
                }

                var model = JsonSerializer.Deserialize<StoreIdentityModel>(decryptedBytes);
                if (model != null && model.StoreId == storeId)
                {
                    lock (_lock)
                    {
                        _storeId = model.StoreId;
                        _installationId = model.InstallationId;
                        _storeSigningKey = Convert.FromHexString(model.StoreSigningKeyHex);
                        _installationSecret = Convert.FromHexString(model.InstallationSecretHex);
                        _masterKeyValidatorHash = Convert.FromHexString(model.MasterKeyValidatorHashHex);
                    }
                    return;
                }
            }
            catch
            {
                // File corrupted; generate fresh identity
            }
        }

        // Generate new local installation identity
        lock (_lock)
        {
            _storeId = storeId;
            _installationId = Guid.NewGuid();
            _installationSecret = RandomNumberGenerator.GetBytes(32);

            // Default signing key until enrolled
            _storeSigningKey = RandomNumberGenerator.GetBytes(32);
            using var hmac = new HMACSHA256(_storeSigningKey);
            _masterKeyValidatorHash = hmac.ComputeHash(Encoding.UTF8.GetBytes("grozet-pos-master-key-validation-v1"));
        }

        await PersistAsync(ct).ConfigureAwait(false);
    }

    public byte[] GetStoreSigningKey()
    {
        lock (_lock)
        {
            return (byte[])_storeSigningKey.Clone();
        }
    }

    public void EnrollReplacementServer(Guid storeId, char[] masterRecoveryKeyHex)
    {
        if (masterRecoveryKeyHex == null || masterRecoveryKeyHex.Length != 64)
        {
            throw new DomainException("StoreMasterRecoveryKey must be a 64-character hexadecimal string.", "INVALID_MASTER_KEY");
        }

        byte[] masterKeyBytes = new byte[32];
        try
        {
            masterKeyBytes = Convert.FromHexString(new string(masterRecoveryKeyHex));
            var signingKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                storeId.ToByteArray(),
                Encoding.UTF8.GetBytes("grozet-pos-store-signing-v1"));

            using var hmac = new HMACSHA256(signingKey);
            var validatorHash = hmac.ComputeHash(Encoding.UTF8.GetBytes("grozet-pos-master-key-validation-v1"));

            lock (_lock)
            {
                _storeId = storeId;
                _installationId = Guid.NewGuid();
                _installationSecret = RandomNumberGenerator.GetBytes(32);
                _storeSigningKey = signingKey;
                _masterKeyValidatorHash = validatorHash;
            }

            PersistAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKeyBytes);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(masterRecoveryKeyHex.AsSpan()));
        }
    }

    public bool ValidateMasterRecoveryKey(char[] masterRecoveryKeyHex)
    {
        if (masterRecoveryKeyHex == null || masterRecoveryKeyHex.Length != 64)
        {
            return false;
        }

        byte[] masterKeyBytes = new byte[32];
        byte[] testSigningKey = new byte[32];
        try
        {
            masterKeyBytes = Convert.FromHexString(new string(masterRecoveryKeyHex));
            testSigningKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                _storeId.ToByteArray(),
                Encoding.UTF8.GetBytes("grozet-pos-store-signing-v1"));

            using var hmac = new HMACSHA256(testSigningKey);
            var testHash = hmac.ComputeHash(Encoding.UTF8.GetBytes("grozet-pos-master-key-validation-v1"));

            byte[] currentHash;
            lock (_lock)
            {
                currentHash = _masterKeyValidatorHash;
            }

            if (currentHash.Length == 0 || testHash.Length != currentHash.Length)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(testHash, currentHash);
        }
        catch
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKeyBytes);
            CryptographicOperations.ZeroMemory(testSigningKey);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(masterRecoveryKeyHex.AsSpan()));
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        StoreIdentityModel model;
        lock (_lock)
        {
            model = new StoreIdentityModel
            {
                StoreId = _storeId,
                InstallationId = _installationId,
                StoreSigningKeyHex = Convert.ToHexString(_storeSigningKey),
                InstallationSecretHex = Convert.ToHexString(_installationSecret),
                MasterKeyValidatorHashHex = Convert.ToHexString(_masterKeyValidatorHash)
            };
        }

        var dir = Path.GetDirectoryName(_securityFilePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var rawBytes = JsonSerializer.SerializeToUtf8Bytes(model);
        byte[] cipherBytes;
        if (OperatingSystem.IsWindows())
        {
            cipherBytes = ProtectedData.Protect(rawBytes, null, DataProtectionScope.LocalMachine);
        }
        else
        {
            cipherBytes = rawBytes;
        }

        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tmpPath = $"{_securityFilePath}.tmp_{Guid.NewGuid():N}";
            await File.WriteAllBytesAsync(tmpPath, cipherBytes, ct).ConfigureAwait(false);

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.Move(tmpPath, _securityFilePath, overwrite: true);
                    break;
                }
                catch (IOException) when (i < 4)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
                catch
                {
                    try { File.Delete(tmpPath); } catch { }
                    break;
                }
            }
        }
        finally
        {
            FileLock.Release();
        }
    }

    private sealed class StoreIdentityModel
    {
        public Guid StoreId { get; set; }
        public Guid InstallationId { get; set; }
        public string StoreSigningKeyHex { get; set; } = string.Empty;
        public string InstallationSecretHex { get; set; } = string.Empty;
        public string MasterKeyValidatorHashHex { get; set; } = string.Empty;
    }
}
