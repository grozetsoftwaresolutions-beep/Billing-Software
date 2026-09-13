using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Users;

public sealed record CreateUserRequest(
    string Username,
    string FullName,
    string Password,
    string Pin,
    RoleType Role);

public sealed record UpdateUserRoleRequest(
    RoleType Role);

public sealed record UserDto(
    Guid Id,
    string Username,
    string FullName,
    RoleType Role,
    bool IsActive,
    bool IsLocked);
