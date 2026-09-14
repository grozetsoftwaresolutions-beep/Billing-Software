using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Configuration;

namespace RetailPOS.Application.Services;

public interface IStoreService
{
    Task<ApiResponse<StoreSettingsDto>> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<ApiResponse<StoreSettingsDto>> UpdateSettingsAsync(UpdateStoreSettingsRequest request, string executingUser, CancellationToken cancellationToken = default);
}
