namespace RetailPOS.Application.Services;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface IStoreIdentityService
{
    Guid StoreId { get; }
    string StoreName { get; }
    Task InitializeOrLoadAsync(Guid storeId, string storeName, CancellationToken ct = default);
    byte[] GetStoreSigningKey();
    void EnrollReplacementServer(Guid storeId, char[] masterRecoveryKeyHex);
    bool ValidateMasterRecoveryKey(char[] masterRecoveryKeyHex);
    bool ValidateMasterRecoveryKeyAgainstVerifier(Guid storeId, char[] masterRecoveryKeyHex, string authoritativeValidatorHex);
    (string MasterKeyValidatorHashHex, string WrappedStoreSigningKeyHex) WrapStoreSigningKey(Guid storeId, char[] masterRecoveryKeyHex, byte[] storeSigningKey, byte[]? customNonce = null);
    byte[] UnwrapStoreSigningKey(Guid storeId, char[] masterRecoveryKeyHex, string wrappedStoreSigningKeyHex);
    Task InitializeNewStoreIdentityAsync(Guid storeId, string storeName, byte[] storeSigningKey, char[] masterRecoveryKeyHex, CancellationToken ct = default);
    Task StageRotatedIdentityAsync(Guid storeId, byte[] storeSigningKey, char[] newMasterRecoveryKeyHex, CancellationToken ct = default);
    void PromoteStagedIdentity();
    void CleanStagedIdentity();
    Task ReloadActiveIdentityAsync(CancellationToken ct = default);
    void CreateIdentitySafetySnapshot(string destinationPath);
    void RestoreIdentitySafetySnapshot(string sourcePath);
}
