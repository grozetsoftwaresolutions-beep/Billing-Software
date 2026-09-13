using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Services;

public interface IUserService
{
    Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken cancellationToken = default);

    Task<ApiResponse<UserDto>> CreateUserAsync(
        CreateUserRequest request,
        Guid executingUserId,
        RoleType executingUserRole,
        string executingUsername,
        CancellationToken cancellationToken = default);

    Task<ApiResponse<object>> DeactivateUserAsync(
        Guid targetUserId,
        Guid executingUserId,
        RoleType executingUserRole,
        string executingUsername,
        CancellationToken cancellationToken = default);

    Task<ApiResponse<object>> ActivateUserAsync(
        Guid targetUserId,
        Guid executingUserId,
        RoleType executingUserRole,
        string executingUsername,
        CancellationToken cancellationToken = default);
}
