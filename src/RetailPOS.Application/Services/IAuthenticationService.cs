using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Application.Services;

public interface IAuthenticationService
{
    Task<SetupStatusResponse> GetSetupStatusAsync(CancellationToken cancellationToken = default);

    Task<ApiResponse<object>> SetupOwnerAsync(SetupOwnerRequest request, CancellationToken cancellationToken = default);

    Task<ApiResponse<LoginResponse>> LoginWithPasswordAsync(LoginPasswordRequest request, CancellationToken cancellationToken = default);

    Task<ApiResponse<LoginResponse>> LoginWithPinAsync(LoginPinRequest request, CancellationToken cancellationToken = default);

    Task<ApiResponse<object>> LogoutAsync(string token, CancellationToken cancellationToken = default);
}
