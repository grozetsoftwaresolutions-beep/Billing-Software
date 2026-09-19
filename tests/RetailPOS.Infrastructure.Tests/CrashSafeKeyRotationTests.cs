using System.IO;
using System.Security.Cryptography;
using RetailPOS.Application.Services;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Security;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class CrashSafeKeyRotationTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _securityDir;
    private readonly string _activeIdentityPath;
    private readonly string _stagedIdentityPath;

    public CrashSafeKeyRotationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "RetailPOS_RotationTests_" + Guid.NewGuid().ToString("N"));
        _securityDir = Path.Combine(_testDir, "Security");
        Directory.CreateDirectory(_securityDir);

        _activeIdentityPath = Path.Combine(_securityDir, "store_identity.dat");
        _stagedIdentityPath = Path.Combine(_securityDir, "store_identity.dat.rotation_staged");
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
    public void StoreDomain_StageRotation_SetsPendingProperties()
    {
        var store = new Store(1, "Test Store");
        var storeGuid = Guid.NewGuid();
        string vA = new string('A', 64);
        string wA = new string('1', 120);
        store.InitializeSecurityRoot(storeGuid, vA, wA);

        string vB = new string('B', 64);
        string wB = new string('2', 120);
        string tokenHash = new string('F', 64);
        var expiresAt = DateTime.UtcNow.AddMinutes(15);

        store.StageRotation(vB, wB, tokenHash, expiresAt, "User123");

        Assert.Equal("ROTATION_STAGED", store.RotationState);
        Assert.Equal(vB, store.PendingRotationValidatorHash);
        Assert.Equal(wB, store.PendingWrappedStoreSigningKey);
        Assert.Equal(vA, store.MasterKeyValidatorHash); // Authoritative remains A
        Assert.Equal(wA, store.WrappedStoreSigningKey);
    }

    [Fact]
    public void StoreDomain_StageRotation_ThrowsIfAlreadyStaged()
    {
        var store = new Store(1, "Test Store");
        store.InitializeSecurityRoot(Guid.NewGuid(), new string('A', 64), new string('1', 120));

        store.StageRotation(new string('B', 64), new string('2', 120), new string('1', 64), DateTime.UtcNow.AddMinutes(15), "User1");

        Assert.Throws<DomainException>(() =>
            store.StageRotation(new string('C', 64), new string('3', 120), new string('2', 64), DateTime.UtcNow.AddMinutes(15), "User2"));
    }

    [Fact]
    public void StoreDomain_CancelRotation_ClearsPendingStatePreservingAuthoritative()
    {
        var store = new Store(1, "Test Store");
        var storeGuid = Guid.NewGuid();
        string vA = new string('A', 64);
        string wA = new string('1', 120);
        store.InitializeSecurityRoot(storeGuid, vA, wA);

        store.StageRotation(new string('B', 64), new string('2', 120), new string('1', 64), DateTime.UtcNow.AddMinutes(15), "User1");
        store.CancelRotation();

        Assert.Null(store.RotationState);
        Assert.Null(store.PendingRotationValidatorHash);
        Assert.Null(store.PendingWrappedStoreSigningKey);
        Assert.Null(store.RotationTokenHash);
        Assert.Equal(vA, store.MasterKeyValidatorHash);
        Assert.Equal(wA, store.WrappedStoreSigningKey);
    }

    [Fact]
    public void StoreDomain_FinalizeRotation_PromotesPendingToAuthoritative()
    {
        var store = new Store(1, "Test Store");
        var storeGuid = Guid.NewGuid();
        string vA = new string('A', 64);
        string wA = new string('1', 120);
        store.InitializeSecurityRoot(storeGuid, vA, wA);

        string vB = new string('B', 64);
        string wB = new string('2', 120);
        store.StageRotation(vB, wB, new string('1', 64), DateTime.UtcNow.AddMinutes(15), "User1");
        store.FinalizeRotation();

        Assert.Null(store.RotationState);
        Assert.Null(store.PendingRotationValidatorHash);
        Assert.Null(store.PendingWrappedStoreSigningKey);
        Assert.Equal(vB, store.MasterKeyValidatorHash); // B is now authoritative
        Assert.Equal(wB, store.WrappedStoreSigningKey);
    }

    [Fact]
    public async Task StoreIdentityService_StageAndPromote_ReplacesIdentityAtomically()
    {
        var service = new StoreIdentityService(_securityDir);
        var storeGuid = Guid.NewGuid();
        var signingKey = RandomNumberGenerator.GetBytes(32);
        var keyA = new string('A', 64).ToCharArray();
        var keyB = new string('B', 64).ToCharArray();

        // 1. Initialize store identity A
        await service.InitializeNewStoreIdentityAsync(storeGuid, "My Store", signingKey, keyA);
        Assert.True(File.Exists(_activeIdentityPath));
        Assert.False(File.Exists(_stagedIdentityPath));

        // 2. Stage rotation to key B
        await service.StageRotatedIdentityAsync(storeGuid, signingKey, keyB);
        Assert.True(File.Exists(_stagedIdentityPath));
        Assert.True(File.Exists(_activeIdentityPath));

        // 3. Promote staged identity
        service.PromoteStagedIdentity();
        await service.ReloadActiveIdentityAsync();
        Assert.True(File.Exists(_activeIdentityPath));
        Assert.False(File.Exists(_stagedIdentityPath));

        // 4. Validate that active identity is now key B
        Assert.True(service.ValidateMasterRecoveryKey(new string('B', 64).ToCharArray()));
    }

    [Fact]
    public async Task StoreIdentityService_CleanStagedIdentity_RemovesStagedFile()
    {
        var service = new StoreIdentityService(_securityDir);
        var storeGuid = Guid.NewGuid();
        var signingKey = RandomNumberGenerator.GetBytes(32);
        var keyA = new string('A', 64).ToCharArray();
        var keyB = new string('B', 64).ToCharArray();

        await service.InitializeNewStoreIdentityAsync(storeGuid, "My Store", signingKey, keyA);
        await service.StageRotatedIdentityAsync(storeGuid, signingKey, keyB);
        Assert.True(File.Exists(_stagedIdentityPath));

        service.CleanStagedIdentity();
        Assert.False(File.Exists(_stagedIdentityPath));
        Assert.True(File.Exists(_activeIdentityPath));
    }

    [Fact]
    public async Task IdentitySafetySnapshot_CreatesAndRestoresSnapshot()
    {
        var service = new StoreIdentityService(_securityDir);
        var storeGuid = Guid.NewGuid();
        var signingKey = RandomNumberGenerator.GetBytes(32);
        var keyA = new string('A', 64).ToCharArray();
        var keyB = new string('B', 64).ToCharArray();

        // 1. Initialize identity A
        await service.InitializeNewStoreIdentityAsync(storeGuid, "Test Store", signingKey, keyA);
        Assert.True(service.ValidateMasterRecoveryKey(new string('A', 64).ToCharArray()));

        // 2. Snapshot
        string snapshotPath = Path.Combine(_testDir, "store_identity_snapshot.bak");
        service.CreateIdentitySafetySnapshot(snapshotPath);
        Assert.True(File.Exists(snapshotPath));

        // 3. Stage and promote B
        await service.StageRotatedIdentityAsync(storeGuid, signingKey, keyB);
        service.PromoteStagedIdentity();
        await service.ReloadActiveIdentityAsync();
        Assert.True(service.ValidateMasterRecoveryKey(new string('B', 64).ToCharArray()));

        // 4. Restore snapshot
        service.RestoreIdentitySafetySnapshot(snapshotPath);
        Assert.True(service.ValidateMasterRecoveryKey(new string('A', 64).ToCharArray()));
    }
}
