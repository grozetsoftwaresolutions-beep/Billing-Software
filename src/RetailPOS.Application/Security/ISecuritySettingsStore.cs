using RetailPOS.Domain.Security;

namespace RetailPOS.Application.Security;

public interface ISecuritySettingsStore
{
    bool IsInFailsafeMode { get; }
    FailsafeActiveMarker? CurrentFailsafeMarker { get; }
    Task InitializeAsync(CancellationToken ct = default);
    Task<SecuritySettings> GetSettingsAsync(CancellationToken ct = default);
    Task UpdateSettingsAsync(SecuritySettings newSettings, Guid ownerUserId, string ownerUsername, CancellationToken ct = default);
    Task ClearFailsafeAsync(SecuritySettings newSettings, Guid ownerUserId, string ownerUsername, Guid? clearOperationId = null, CancellationToken ct = default);
    void EnterFailsafeMode(string reason);
}
