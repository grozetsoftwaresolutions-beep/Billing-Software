using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Auth;

public sealed record SetupStatusResponse(
    bool IsSetupRequired,
    string StoreName);

public sealed record SetupOwnerRequest(
    string StoreName,
    string Username,
    string FullName,
    string Password,
    string Pin);

public sealed record SetupOwnerResponse(
    string StoreMasterRecoveryKey,
    Guid StoreGuid,
    string StoreName,
    string Message);


public sealed record LoginPasswordRequest(
    string Username,
    string Password,
    Guid? TerminalId = null);

public sealed record LoginPinRequest(
    string Username,
    string Pin,
    Guid TerminalId);

public sealed record LoginResponse(
    string Token,
    Guid UserId,
    string Username,
    string FullName,
    RoleType Role,
    Guid? TerminalId,
    DateTime ExpiresAtUtc);

public sealed record SessionStatusResponse(
    bool IsValid,
    Guid? UserId,
    string? Username,
    string? FullName,
    RoleType? Role,
    Guid? TerminalId,
    DateTime? ExpiresAtUtc);
