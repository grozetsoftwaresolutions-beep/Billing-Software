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
}
