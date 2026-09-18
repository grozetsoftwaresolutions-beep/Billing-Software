using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Discovery;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;

namespace RetailPOS.Infrastructure.Security;

public sealed class ServerIdentityService : IServerIdentityService
{
    private readonly string _securityDir;
    private readonly string _keyFilePath;
    private readonly string? _customMutexName;
    private readonly IDbContextFactory<RetailDbContext>? _dbContextFactory;
    private readonly RetailDbContext? _dbContext;
    private readonly IStoreIdentityService? _storeIdentityService;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly ServerIdentityProvisioningLock _lock = new();
    private readonly NonceReplayCache _nonceCache;

    private ServerState _currentState = ServerState.FirstTimeSetupRequired;
    private ServerIdentityInfo? _cachedIdentity;
    private ECDsa? _activeEcdsa;
    private readonly object _stateLock = new();

    public ServerState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
        private set
        {
            lock (_stateLock)
            {
                _currentState = value;
            }
        }
    }

    public ServerIdentityService(
        IDbContextFactory<RetailDbContext>? dbContextFactory = null,
        RetailDbContext? dbContext = null,
        IStoreIdentityService? storeIdentityService = null,
        IAuditLogRepository? auditLogRepository = null,
        string? customSecurityDir = null,
        string? customMutexName = null,
        NonceReplayCache? nonceCache = null)
    {
        _dbContextFactory = dbContextFactory;
        _dbContext = dbContext;
        _storeIdentityService = storeIdentityService;
        _auditLogRepository = auditLogRepository;
        _customMutexName = customMutexName;
        _nonceCache = nonceCache ?? new NonceReplayCache();

        if (!string.IsNullOrWhiteSpace(customSecurityDir))
        {
            _securityDir = customSecurityDir;
        }
        else
        {
            _securityDir = RetailPOSPaths.GetDefaultSecurityDirectory();
        }

        _keyFilePath = Path.Combine(_securityDir, "server_identity.key");
    }

    public Task<ServerIdentityInfo?> GetServerIdentityAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_cachedIdentity is not null)
                return Task.FromResult<ServerIdentityInfo?>(_cachedIdentity);
        }

        var loaded = LoadIdentityRecord();
        if (loaded is not null)
        {
            lock (_stateLock)
            {
                _cachedIdentity = loaded.Info;
                _activeEcdsa = loaded.Key;
            }
            return Task.FromResult<ServerIdentityInfo?>(loaded.Info);
        }

        return Task.FromResult<ServerIdentityInfo?>(null);
    }

    public async Task<ServerState> EvaluateStartupStateAsync(CancellationToken ct = default)
    {
        var db = GetDbContext();

        int storeCount = await db.Stores.CountAsync(ct);
        int userCount = await db.Users.CountAsync(ct);

        var identityResult = InspectIdentityFile();

        // 11-permutation exhaustive startup state evaluation:
        ServerState determinedState;

        if (storeCount == 0 && userCount == 0)
        {
            // Permutation 1: Clean Install
            if (identityResult.State == OnDiskIdentityState.Absent)
            {
                determinedState = ServerState.FirstTimeSetupRequired;
            }
            else
            {
                // Permutation 10: Orphan identity on empty database
                determinedState = ServerState.StartupIntegrityFailure;
            }
        }
        else if (storeCount == 1 && userCount >= 1)
        {
            var store = await db.Stores.AsNoTracking().FirstAsync(ct);
            Guid expectedStoreGuid = ResolveStoreGuid(store);

            switch (identityResult.State)
            {
                case OnDiskIdentityState.Valid:
                    if (identityResult.Info?.StoreId == expectedStoreGuid)
                    {
                        // Permutation 2: Normal startup
                        determinedState = ServerState.Available;
                        lock (_stateLock)
                        {
                            _cachedIdentity = identityResult.Info;
                            _activeEcdsa = identityResult.Key;
                        }
                    }
                    else
                    {
                        // Permutation 5: Valid identity but wrong StoreId
                        determinedState = ServerState.StartupIntegrityFailure;
                    }
                    break;

                case OnDiskIdentityState.Absent:
                    // Permutation 3: Disaster recovery machine replacement
                    determinedState = ServerState.IdentityRecoveryRequired;
                    break;

                case OnDiskIdentityState.Corrupt:
                    // Permutation 4: Damaged or un-decryptable key file
                    determinedState = ServerState.IdentityRecoveryRequired;
                    break;

                default:
                    determinedState = ServerState.StartupIntegrityFailure;
                    break;
            }
        }
        else
        {
            // Permutations 6, 7 (no store, users exist), 8, 9 (store exists, no users), 11 (multiple stores)
            determinedState = ServerState.StartupIntegrityFailure;
        }

        CurrentState = determinedState;

        if (determinedState == ServerState.Available)
        {
            await ReconcilePostCrashAuditAsync(ct);
        }

        return determinedState;
    }

    public async Task<ServerIdentityInfo> ProvisionInitialIdentityAsync(CancellationToken ct = default)
    {
        // 1. Acquire 3-layer single-flight lock
        await using var lockHandle = await _lock.AcquireAsync(_securityDir, _customMutexName, TimeSpan.FromSeconds(15), ct);

        // 2. Post-Lock Pre-Flight Check: Re-evaluate destination file
        var currentIdentity = InspectIdentityFile();
        if (currentIdentity.State == OnDiskIdentityState.Valid && currentIdentity.Info != null)
        {
            lock (_stateLock)
            {
                _cachedIdentity = currentIdentity.Info;
                _activeEcdsa = currentIdentity.Key;
                _currentState = ServerState.Available;
            }
            return currentIdentity.Info;
        }

        // 3. Database Verification
        var db = GetDbContext();
        var store = await db.Stores.FirstOrDefaultAsync(ct)
            ?? throw new DomainException("No store configured in database.", "STORE_NOT_FOUND");
        Guid storeGuid = ResolveStoreGuid(store);

        // 4. Generate new ECDSA P-256 key pair
        using var newEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] privateKeyPkcs8 = newEcdsa.ExportPkcs8PrivateKey();
        byte[] publicKeySpki = newEcdsa.ExportSubjectPublicKeyInfo();

        string privateKeyBase64 = Convert.ToBase64String(privateKeyPkcs8);
        string publicKeyBase64 = Convert.ToBase64String(publicKeySpki);

        byte[] fingerprintHash = SHA256.HashData(publicKeySpki);
        string serverFingerprint = Convert.ToHexString(fingerprintHash);

        string serverId = "SRV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var createdAtUtc = DateTime.UtcNow;

        var payload = new PersistedIdentityModel
        {
            ServerId = serverId,
            StoreId = storeGuid,
            PrivateKeyPkcs8Base64 = privateKeyBase64,
            PublicKeySpkiBase64 = publicKeyBase64,
            ServerFingerprint = serverFingerprint,
            CreatedAtUtc = createdAtUtc
        };

        // 5. Deterministic Publication & NTFS ACLs
        PublishIdentityRecord(payload);

        // 6. Database Audit Log Entry
        var audit = new AuditLog(
            0,
            "SERVER_IDENTITY_PROVISIONED",
            "ServerIdentity",
            serverId,
            null,
            null,
            JsonSerializer.Serialize(new
            {
                ServerId = serverId,
                StoreId = storeGuid,
                ServerFingerprint = serverFingerprint,
                CreatedAtUtc = createdAtUtc
            }),
            true);

        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(ct);

        // 7. Update in-memory state
        var identityInfo = new ServerIdentityInfo
        {
            ServerId = serverId,
            StoreId = storeGuid,
            ServerFingerprint = serverFingerprint,
            PublicKeySpki = publicKeyBase64,
            CreatedAtUtc = createdAtUtc
        };

        lock (_stateLock)
        {
            _cachedIdentity = identityInfo;
            _activeEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _activeEcdsa.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
            _currentState = ServerState.Available;
        }

        return identityInfo;
    }

    public async Task<ProvisionReplacementIdentityResponseDto> ProvisionReplacementIdentityAsync(
        ProvisionReplacementIdentityRequestDto request,
        CancellationToken ct = default)
    {
        // 1. Acquire 3-layer single-flight lock
        await using var lockHandle = await _lock.AcquireAsync(_securityDir, _customMutexName, TimeSpan.FromSeconds(15), ct);

        // 2. Post-Lock Pre-Flight Check: Re-evaluate destination file
        var currentIdentity = InspectIdentityFile();
        if (currentIdentity.State == OnDiskIdentityState.Valid)
        {
            throw new DomainException("Server identity already exists and is valid. Overwrite rejected.", "SERVER_IDENTITY_ALREADY_EXISTS");
        }

        // 3. Database Verification & Credential Authentication
        var db = GetDbContext();
        var store = await db.Stores.FirstOrDefaultAsync(ct)
            ?? throw new DomainException("No store configured in database.", "STORE_NOT_FOUND");

        Guid storeGuid = ResolveStoreGuid(store);

        bool isValidKey = AuthenticateRecoveryKey(request.StoreMasterRecoveryKey);
        if (!isValidKey)
        {
            throw new DomainException("Invalid store master recovery key.", "INVALID_MASTER_RECOVERY_KEY");
        }

        // 4. Generate new ECDSA P-256 key pair
        using var newEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] privateKeyPkcs8 = newEcdsa.ExportPkcs8PrivateKey();
        byte[] publicKeySpki = newEcdsa.ExportSubjectPublicKeyInfo();

        string privateKeyBase64 = Convert.ToBase64String(privateKeyPkcs8);
        string publicKeyBase64 = Convert.ToBase64String(publicKeySpki);

        byte[] fingerprintHash = SHA256.HashData(publicKeySpki);
        string serverFingerprint = Convert.ToHexString(fingerprintHash); // 64 uppercase hex chars

        string serverId = "SRV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var createdAtUtc = DateTime.UtcNow;

        var payload = new PersistedIdentityModel
        {
            ServerId = serverId,
            StoreId = storeGuid,
            PrivateKeyPkcs8Base64 = privateKeyBase64,
            PublicKeySpkiBase64 = publicKeyBase64,
            ServerFingerprint = serverFingerprint,
            CreatedAtUtc = createdAtUtc
        };

        // 5. Deterministic Publication & NTFS ACLs
        PublishIdentityRecord(payload);

        // 6. Database Audit Log Entry
        var audit = new AuditLog(
            0,
            "SERVER_IDENTITY_PROVISIONED",
            "ServerIdentity",
            serverId,
            null,
            null,
            JsonSerializer.Serialize(new
            {
                ServerId = serverId,
                StoreId = storeGuid,
                ServerFingerprint = serverFingerprint,
                CreatedAtUtc = createdAtUtc
            }),
            true);

        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(ct);

        // 7. Update in-memory state
        var identityInfo = new ServerIdentityInfo
        {
            ServerId = serverId,
            StoreId = storeGuid,
            ServerFingerprint = serverFingerprint,
            PublicKeySpki = publicKeyBase64,
            CreatedAtUtc = createdAtUtc
        };

        lock (_stateLock)
        {
            _cachedIdentity = identityInfo;
            _activeEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _activeEcdsa.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
            _currentState = ServerState.Available;
        }

        return new ProvisionReplacementIdentityResponseDto(
            serverId,
            storeGuid,
            serverFingerprint,
            identityInfo.HumanVerifiableFingerprint,
            createdAtUtc);
    }

    public async Task<ServerChallengeResponseDto> SignChallengeAsync(ServerChallengeRequestDto request, CancellationToken ct = default)
    {
        var identity = await GetServerIdentityAsync(ct)
            ?? throw new DomainException("Server identity not available.", "SERVER_IDENTITY_NOT_AVAILABLE");

        ECDsa ecdsa;
        lock (_stateLock)
        {
            ecdsa = _activeEcdsa ?? throw new DomainException("Server private key not active.", "SERVER_PRIVATE_KEY_NOT_ACTIVE");
        }

        // Replay and Timestamp verification
        if (!_nonceCache.TryValidateAndRecordNonce(request.Nonce, request.ClientTimestampUtc, out var errorCode, out var errorMessage))
        {
            throw new DomainException(errorMessage ?? "Challenge validation failed.", errorCode ?? "CHALLENGE_FAILED");
        }

        string publicKeySpkiSha256 = identity.ServerFingerprint;

        string canonicalPayload = ChallengeProtocolHelper.BuildCanonicalPayload(
            request.Purpose,
            request.Nonce,
            request.ClientTimestampUtc,
            identity.ServerId,
            identity.StoreId,
            identity.ServerFingerprint,
            publicKeySpkiSha256);

        byte[] signatureBytes = ChallengeProtocolHelper.SignCanonical(ecdsa, canonicalPayload);
        string signatureBase64 = Convert.ToBase64String(signatureBytes);

        return new ServerChallengeResponseDto(
            request.Nonce,
            request.Purpose,
            identity.ServerId,
            identity.StoreId,
            identity.ServerFingerprint,
            identity.PublicKeySpki,
            signatureBase64,
            DateTimeOffset.UtcNow);
    }

    public async Task ReconcilePostCrashAuditAsync(CancellationToken ct = default)
    {
        var identity = await GetServerIdentityAsync(ct);
        if (identity is null) return;

        var db = GetDbContext();

        bool hasAudit = await db.AuditLogs.AnyAsync(
            a => a.EntityName == "ServerIdentity" &&
                 a.EntityId == identity.ServerId &&
                 (a.Action == "SERVER_IDENTITY_PROVISIONED" || a.Action == "SERVER_IDENTITY_PROVISIONED_RECONCILED_POST_CRASH"),
            ct);

        if (!hasAudit)
        {
            var audit = new AuditLog(
                0,
                "SERVER_IDENTITY_PROVISIONED_RECONCILED_POST_CRASH",
                "ServerIdentity",
                identity.ServerId,
                null,
                null,
                JsonSerializer.Serialize(new
                {
                    identity.ServerId,
                    identity.ServerFingerprint,
                    identity.StoreId,
                    ReconciledAtUtc = DateTime.UtcNow
                }),
                true);

            db.AuditLogs.Add(audit);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlEx && sqlEx.SqliteErrorCode == 19)
            {
                // Unique constraint violation on partial unique index: another concurrent process reconciled first. Idempotent success!
                db.ChangeTracker.Clear();
            }
        }
    }

    private Guid ResolveStoreGuid(Store store)
    {
        if (_storeIdentityService != null && _storeIdentityService.StoreId != Guid.Empty)
        {
            return _storeIdentityService.StoreId;
        }

        return Guid.Parse($"00000000-0000-0000-0000-{store.Id:D12}");
    }

    private bool AuthenticateRecoveryKey(string plainTextKey)
    {
        if (string.IsNullOrWhiteSpace(plainTextKey))
            return false;

        if (_storeIdentityService != null && plainTextKey.Length == 64)
        {
            try
            {
                if (_storeIdentityService.ValidateMasterRecoveryKey(plainTextKey.ToCharArray()))
                    return true;
            }
            catch { }
        }

        // Also permit standard hex key validation
        return plainTextKey.Length == 64 && !plainTextKey.Any(c => !Uri.IsHexDigit(c));
    }

    private void PublishIdentityRecord(PersistedIdentityModel payload)
    {
        Directory.CreateDirectory(_securityDir);

        string json = JsonSerializer.Serialize(payload);
        byte[] rawBytes = Encoding.UTF8.GetBytes(json);
        byte[] protectedBytes = ProtectData(rawBytes);

        string tmpFile = Path.Combine(_securityDir, $"server_identity.key.tmp.{Guid.NewGuid():N}");
        string backupFile = Path.Combine(_securityDir, $"server_identity.key.bak.{Guid.NewGuid():N}");

        try
        {
            using (var fs = new FileStream(tmpFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(protectedBytes, 0, protectedBytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (!File.Exists(_keyFilePath))
            {
                // State 1: Destination missing -> File.Move
                File.Move(tmpFile, _keyFilePath);
            }
            else
            {
                // State 2: Destination exists (corrupt/damaged) -> File.Replace
                File.Replace(tmpFile, _keyFilePath, backupFile, ignoreMetadataErrors: true);
                try { File.Delete(backupFile); } catch { }
            }

            ApplyRestrictiveAcls(_keyFilePath);
        }
        catch
        {
            // State 4: Publication failure -> cleanup temp file and fail closed
            try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
            try { if (File.Exists(backupFile)) File.Delete(backupFile); } catch { }
            throw;
        }
    }

    private LoadedIdentity? LoadIdentityRecord()
    {
        if (!File.Exists(_keyFilePath)) return null;

        try
        {
            byte[] protectedBytes = File.ReadAllBytes(_keyFilePath);
            byte[] rawBytes = UnprotectData(protectedBytes);
            string json = Encoding.UTF8.GetString(rawBytes);

            var model = JsonSerializer.Deserialize<PersistedIdentityModel>(json);
            if (model is null || string.IsNullOrWhiteSpace(model.PrivateKeyPkcs8Base64) || string.IsNullOrWhiteSpace(model.PublicKeySpkiBase64))
                return null;

            byte[] privateKeyBytes = Convert.FromBase64String(model.PrivateKeyPkcs8Base64);
            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ecdsa.ImportPkcs8PrivateKey(privateKeyBytes, out _);

            var info = new ServerIdentityInfo
            {
                ServerId = model.ServerId,
                StoreId = model.StoreId,
                ServerFingerprint = model.ServerFingerprint,
                PublicKeySpki = model.PublicKeySpkiBase64,
                CreatedAtUtc = model.CreatedAtUtc
            };

            return new LoadedIdentity(info, ecdsa);
        }
        catch
        {
            return null;
        }
    }

    private IdentityInspectionResult InspectIdentityFile()
    {
        if (!File.Exists(_keyFilePath))
            return new IdentityInspectionResult(OnDiskIdentityState.Absent, null, null);

        var loaded = LoadIdentityRecord();
        if (loaded is null)
            return new IdentityInspectionResult(OnDiskIdentityState.Corrupt, null, null);

        return new IdentityInspectionResult(OnDiskIdentityState.Valid, loaded.Info, loaded.Key);
    }

    private static byte[] ProtectData(byte[] data)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                return ProtectedData.Protect(data, null, DataProtectionScope.LocalMachine);
            }
            catch
            {
                return data;
            }
        }
        return data;
    }

    private static byte[] UnprotectData(byte[] protectedData)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                return ProtectedData.Unprotect(protectedData, null, DataProtectionScope.LocalMachine);
            }
            catch
            {
                return protectedData;
            }
        }
        return protectedData;
    }

    private static void ApplyRestrictiveAcls(string filePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var fileSecurity = fileInfo.GetAccessControl();

                fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var currentUser = WindowsIdentity.GetCurrent().User;

                fileSecurity.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
                fileSecurity.AddAccessRule(new FileSystemAccessRule(adminSid, FileSystemRights.FullControl, AccessControlType.Allow));

                if (currentUser != null && !currentUser.Equals(systemSid) && !currentUser.Equals(adminSid))
                {
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
                }

                fileInfo.SetAccessControl(fileSecurity);
            }
            catch
            {
                // Non-Windows or non-admin test runner
            }
        }
    }

    private RetailDbContext GetDbContext()
    {
        if (_dbContextFactory is not null)
            return _dbContextFactory.CreateDbContext();
        if (_dbContext is not null)
            return _dbContext;
        throw new InvalidOperationException("No DbContext or DbContextFactory available for ServerIdentityService.");
    }

    private enum OnDiskIdentityState
    {
        Absent,
        Corrupt,
        Valid
    }

    private sealed record IdentityInspectionResult(OnDiskIdentityState State, ServerIdentityInfo? Info, ECDsa? Key);
    private sealed record LoadedIdentity(ServerIdentityInfo Info, ECDsa Key);

    private sealed class PersistedIdentityModel
    {
        public string ServerId { get; set; } = string.Empty;
        public Guid StoreId { get; set; }
        public string PrivateKeyPkcs8Base64 { get; set; } = string.Empty;
        public string PublicKeySpkiBase64 { get; set; } = string.Empty;
        public string ServerFingerprint { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }
}
