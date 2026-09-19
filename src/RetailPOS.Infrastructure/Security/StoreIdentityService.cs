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
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Common;

public sealed class StoreIdentityService : IStoreIdentityService
{
    private static readonly byte[] InfoKeyWrapping = Encoding.UTF8.GetBytes("grozet-pos-store-key-wrapping-v1");
    private static readonly byte[] DomainValidator = Encoding.UTF8.GetBytes("grozet-pos-master-key-validation-v1");
    private static readonly byte[] DomainSigningKeyAad = Encoding.UTF8.GetBytes("grozet-pos-store-signing-key-v1"); // 31 bytes
    private const byte VersionByte = 0x01;

    private readonly string _securityFilePath;
    private readonly string _stagedSecurityFilePath;
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
        var baseDir = !string.IsNullOrWhiteSpace(customSecurityDir)
            ? customSecurityDir
            : RetailPOSPaths.GetDefaultSecurityDirectory();
        _securityFilePath = Path.Combine(baseDir, "store_identity.dat");
        _stagedSecurityFilePath = Path.Combine(baseDir, "store_identity.dat.rotation_staged");
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
            _masterKeyValidatorHash = hmac.ComputeHash(DomainValidator);
        }

        await PersistToFileAsync(_securityFilePath, ct).ConfigureAwait(false);
    }

    public byte[] GetStoreSigningKey()
    {
        lock (_lock)
        {
            return (byte[])_storeSigningKey.Clone();
        }
    }

    public static byte[] BuildAad(Guid storeId)
    {
        byte[] storeIdBytes = storeId.ToByteArray(); // 16 bytes
        byte[] aad = new byte[16 + DomainSigningKeyAad.Length + 1]; // 16 + 31 + 1 = 48 bytes
        Buffer.BlockCopy(storeIdBytes, 0, aad, 0, 16);
        Buffer.BlockCopy(DomainSigningKeyAad, 0, aad, 16, DomainSigningKeyAad.Length);
        aad[^1] = VersionByte;
        return aad;
    }

    public (string MasterKeyValidatorHashHex, string WrappedStoreSigningKeyHex) WrapStoreSigningKey(
        Guid storeId,
        char[] masterRecoveryKeyHex,
        byte[] storeSigningKey,
        byte[]? customNonce = null)
    {
        if (masterRecoveryKeyHex == null || masterRecoveryKeyHex.Length != 64)
            throw new DomainException("StoreMasterRecoveryKey must be a 64-character hexadecimal string.", "INVALID_MASTER_KEY");
        if (storeSigningKey == null || storeSigningKey.Length != 32)
            throw new DomainException("StoreSigningKey must be exactly 32 bytes.", "INVALID_SIGNING_KEY");

        byte[] masterKeyBytes = new byte[32];
        byte[] kwk = new byte[32];
        byte[] nonce = customNonce ?? RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[32];

        try
        {
            masterKeyBytes = Convert.FromHexString(new string(masterRecoveryKeyHex));
            kwk = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                storeId.ToByteArray(),
                InfoKeyWrapping);

            using var hmac = new HMACSHA256(kwk);
            byte[] validatorHash = hmac.ComputeHash(DomainValidator);

            byte[] aad = BuildAad(storeId);
            using (var aesGcm = new AesGcm(kwk, 16))
            {
                aesGcm.Encrypt(nonce, storeSigningKey, ciphertext, tag, aad);
            }

            byte[] wrapped = new byte[12 + 16 + 32];
            Buffer.BlockCopy(nonce, 0, wrapped, 0, 12);
            Buffer.BlockCopy(tag, 0, wrapped, 12, 16);
            Buffer.BlockCopy(ciphertext, 0, wrapped, 28, 32);

            return (Convert.ToHexString(validatorHash), Convert.ToHexString(wrapped));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKeyBytes);
            CryptographicOperations.ZeroMemory(kwk);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            if (customNonce == null)
            {
                CryptographicOperations.ZeroMemory(nonce);
            }
        }
    }

    public byte[] UnwrapStoreSigningKey(Guid storeId, char[] masterRecoveryKeyHex, string wrappedStoreSigningKeyHex)
    {
        if (masterRecoveryKeyHex == null || masterRecoveryKeyHex.Length != 64)
            throw new DomainException("StoreMasterRecoveryKey must be a 64-character hexadecimal string.", "INVALID_MASTER_KEY");
        if (string.IsNullOrWhiteSpace(wrappedStoreSigningKeyHex) || wrappedStoreSigningKeyHex.Length != 120)
            throw new DomainException("WrappedStoreSigningKey must be 120 hexadecimal characters (60 bytes).", "INVALID_WRAPPED_KEY");

        byte[] masterKeyBytes = new byte[32];
        byte[] kwk = new byte[32];
        byte[] wrapped = new byte[60];
        byte[] plaintextS = new byte[32];

        try
        {
            masterKeyBytes = Convert.FromHexString(new string(masterRecoveryKeyHex));
            wrapped = Convert.FromHexString(wrappedStoreSigningKeyHex);

            kwk = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                storeId.ToByteArray(),
                InfoKeyWrapping);

            byte[] nonce = wrapped[..12];
            byte[] tag = wrapped[12..28];
            byte[] ciphertext = wrapped[28..60];
            byte[] aad = BuildAad(storeId);

            using (var aesGcm = new AesGcm(kwk, 16))
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintextS, aad);
            }

            return (byte[])plaintextS.Clone();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKeyBytes);
            CryptographicOperations.ZeroMemory(kwk);
            CryptographicOperations.ZeroMemory(wrapped);
            CryptographicOperations.ZeroMemory(plaintextS);
        }
    }

    public void EnrollReplacementServer(Guid storeId, char[] masterRecoveryKeyHex)
    {
        if (masterRecoveryKeyHex == null || masterRecoveryKeyHex.Length != 64)
        {
            throw new DomainException("StoreMasterRecoveryKey must be a 64-character hexadecimal string.", "INVALID_MASTER_KEY");
        }

        byte[] masterKeyBytes = new byte[32];
        byte[] kwk = new byte[32];
        try
        {
            masterKeyBytes = Convert.FromHexString(new string(masterRecoveryKeyHex));

            // Envelope key wrapping derivation
            kwk = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                storeId.ToByteArray(),
                InfoKeyWrapping);

            using var hmac = new HMACSHA256(kwk);
            var validatorHash = hmac.ComputeHash(DomainValidator);

            // Reconstruct signing key deterministically or generate fresh
            var signingKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                storeId.ToByteArray(),
                Encoding.UTF8.GetBytes("grozet-pos-store-signing-v1"));

            lock (_lock)
            {
                _storeId = storeId;
                _installationId = Guid.NewGuid();
                _installationSecret = RandomNumberGenerator.GetBytes(32);
                _storeSigningKey = signingKey;
                _masterKeyValidatorHash = validatorHash;
            }

            PersistToFileAsync(_securityFilePath, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKeyBytes);
            CryptographicOperations.ZeroMemory(kwk);
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
        byte[] kwk = new byte[32];
        byte[] testLegacyKey = new byte[32];
        try
        {
            masterKeyBytes = Convert.FromHexString(new string(masterRecoveryKeyHex));
            kwk = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                _storeId.ToByteArray(),
                InfoKeyWrapping);

            using var hmac = new HMACSHA256(kwk);
            var testHash = hmac.ComputeHash(DomainValidator);

            byte[] currentHash;
            lock (_lock)
            {
                currentHash = _masterKeyValidatorHash;
            }

            if (currentHash.Length > 0 && testHash.Length == currentHash.Length &&
                CryptographicOperations.FixedTimeEquals(testHash, currentHash))
            {
                return true;
            }

            // Fallback check for Phase 11 legacy direct signing key derivation
            testLegacyKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                _storeId.ToByteArray(),
                Encoding.UTF8.GetBytes("grozet-pos-store-signing-v1"));

            using var legacyHmac = new HMACSHA256(testLegacyKey);
            var legacyHash = legacyHmac.ComputeHash(DomainValidator);

            if (currentHash.Length > 0 && legacyHash.Length == currentHash.Length &&
                CryptographicOperations.FixedTimeEquals(legacyHash, currentHash))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKeyBytes);
            CryptographicOperations.ZeroMemory(kwk);
            CryptographicOperations.ZeroMemory(testLegacyKey);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(masterRecoveryKeyHex.AsSpan()));
        }
    }

    public bool ValidateMasterRecoveryKeyAgainstVerifier(Guid storeId, char[] masterRecoveryKeyHex, string authoritativeValidatorHex)
    {
        if (masterRecoveryKeyHex == null || masterRecoveryKeyHex.Length != 64 ||
            string.IsNullOrWhiteSpace(authoritativeValidatorHex) || authoritativeValidatorHex.Length != 64)
        {
            return false;
        }

        byte[] masterKeyBytes = new byte[32];
        byte[] kwk = new byte[32];
        byte[] expectedHash = new byte[32];
        byte[] testLegacyKey = new byte[32];
        try
        {
            masterKeyBytes = Convert.FromHexString(new string(masterRecoveryKeyHex));
            expectedHash = Convert.FromHexString(authoritativeValidatorHex);

            // Envelope key wrapping check
            kwk = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                storeId.ToByteArray(),
                InfoKeyWrapping);

            using var hmac = new HMACSHA256(kwk);
            var testHash = hmac.ComputeHash(DomainValidator);

            if (CryptographicOperations.FixedTimeEquals(testHash, expectedHash))
            {
                return true;
            }

            // Legacy direct signing key check
            testLegacyKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                storeId.ToByteArray(),
                Encoding.UTF8.GetBytes("grozet-pos-store-signing-v1"));

            using var legacyHmac = new HMACSHA256(testLegacyKey);
            var legacyHash = legacyHmac.ComputeHash(DomainValidator);

            return CryptographicOperations.FixedTimeEquals(legacyHash, expectedHash);
        }
        catch
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKeyBytes);
            CryptographicOperations.ZeroMemory(kwk);
            CryptographicOperations.ZeroMemory(expectedHash);
            CryptographicOperations.ZeroMemory(testLegacyKey);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(masterRecoveryKeyHex.AsSpan()));
        }
    }

    public async Task InitializeNewStoreIdentityAsync(
        Guid storeId,
        string storeName,
        byte[] storeSigningKey,
        char[] masterRecoveryKeyHex,
        CancellationToken ct = default)
    {
        byte[] masterKeyBytes = new byte[32];
        byte[] kwk = new byte[32];
        try
        {
            masterKeyBytes = Convert.FromHexString(new string(masterRecoveryKeyHex));
            kwk = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                storeId.ToByteArray(),
                InfoKeyWrapping);

            using var hmac = new HMACSHA256(kwk);
            var validatorHash = hmac.ComputeHash(DomainValidator);

            lock (_lock)
            {
                _storeId = storeId;
                _storeName = storeName;
                _installationId = Guid.NewGuid();
                _installationSecret = RandomNumberGenerator.GetBytes(32);
                _storeSigningKey = (byte[])storeSigningKey.Clone();
                _masterKeyValidatorHash = validatorHash;
            }

            await PersistToFileAsync(_securityFilePath, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKeyBytes);
            CryptographicOperations.ZeroMemory(kwk);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(masterRecoveryKeyHex.AsSpan()));
        }
    }

    public async Task StageRotatedIdentityAsync(
        Guid storeId,
        byte[] storeSigningKey,
        char[] newMasterRecoveryKeyHex,
        CancellationToken ct = default)
    {
        byte[] masterKeyBytes = new byte[32];
        byte[] kwk = new byte[32];
        try
        {
            masterKeyBytes = Convert.FromHexString(new string(newMasterRecoveryKeyHex));
            kwk = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterKeyBytes,
                32,
                storeId.ToByteArray(),
                InfoKeyWrapping);

            using var hmac = new HMACSHA256(kwk);
            var validatorHash = hmac.ComputeHash(DomainValidator);

            Guid currentInstId;
            byte[] currentInstSecret;
            lock (_lock)
            {
                currentInstId = _installationId != Guid.Empty ? _installationId : Guid.NewGuid();
                currentInstSecret = _installationSecret.Length > 0 ? (byte[])_installationSecret.Clone() : RandomNumberGenerator.GetBytes(32);
            }

            var model = new StoreIdentityModel
            {
                StoreId = storeId,
                InstallationId = currentInstId,
                StoreSigningKeyHex = Convert.ToHexString(storeSigningKey),
                InstallationSecretHex = Convert.ToHexString(currentInstSecret),
                MasterKeyValidatorHashHex = Convert.ToHexString(validatorHash)
            };

            await PersistModelToFileAsync(_stagedSecurityFilePath, model, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKeyBytes);
            CryptographicOperations.ZeroMemory(kwk);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(newMasterRecoveryKeyHex.AsSpan()));
        }
    }

    public void PromoteStagedIdentity()
    {
        if (!File.Exists(_stagedSecurityFilePath))
        {
            if (File.Exists(_securityFilePath))
            {
                // Already promoted
                return;
            }
            throw new DomainException("Staged store identity file not found.", "STAGED_IDENTITY_NOT_FOUND");
        }

        for (int i = 0; i < 5; i++)
        {
            try
            {
                File.Move(_stagedSecurityFilePath, _securityFilePath, overwrite: true);
                return;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void CleanStagedIdentity()
    {
        if (File.Exists(_stagedSecurityFilePath))
        {
            try { File.Delete(_stagedSecurityFilePath); } catch { }
        }
    }

    public async Task ReloadActiveIdentityAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_securityFilePath))
            return;

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
        if (model != null)
        {
            lock (_lock)
            {
                _storeId = model.StoreId;
                _installationId = model.InstallationId;
                _storeSigningKey = Convert.FromHexString(model.StoreSigningKeyHex);
                _installationSecret = Convert.FromHexString(model.InstallationSecretHex);
                _masterKeyValidatorHash = Convert.FromHexString(model.MasterKeyValidatorHashHex);
            }
        }
    }

    public void CreateIdentitySafetySnapshot(string destinationPath)
    {
        if (File.Exists(_securityFilePath))
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.Copy(_securityFilePath, destinationPath, overwrite: true);
        }
    }

    public void RestoreIdentitySafetySnapshot(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, _securityFilePath, overwrite: true);
            ReloadActiveIdentityAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private async Task PersistToFileAsync(string filePath, CancellationToken ct)
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

        await PersistModelToFileAsync(filePath, model, ct).ConfigureAwait(false);
    }

    private static async Task PersistModelToFileAsync(string filePath, StoreIdentityModel model, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(filePath);
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
            var tmpPath = $"{filePath}.tmp_{Guid.NewGuid():N}";
            await using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await fs.WriteAsync(cipherBytes, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.Move(tmpPath, filePath, overwrite: true);
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
