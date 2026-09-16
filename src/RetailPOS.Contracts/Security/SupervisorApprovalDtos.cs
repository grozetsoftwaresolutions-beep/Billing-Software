using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Security;

public sealed record SupervisorApprovalRequest(
    string Username,
    string? Pin,
    string? Password,
    Permission RequiredPermission,
    string EntityType,
    string? EntityId,
    string? RequestNonce,
    string Reason);

public sealed record SupervisorApprovalResponse(
    string ApprovalToken,
    Guid ApprovalTokenId,
    DateTime ExpiresAtUtc,
    Guid SupervisorUserId,
    string SupervisorUsername,
    Permission RequiredPermission,
    string EntityType,
    string? EntityId,
    string? RequestNonce);

public sealed record ConsumeApprovalTokenRequest(
    string ApprovalToken,
    string EntityType,
    string? EntityId,
    string? RequestNonce);

public sealed record TerminalLockRequest(
    string? Reason);

public sealed record TerminalUnlockRequest(
    string Pin,
    string? SupervisorUsername = null,
    string? SupervisorPassword = null);

public sealed record TerminalUnlockResponse(
    bool Success,
    string Message,
    bool UnlockedBySupervisor,
    string? CashierUsername,
    string? SupervisorUsername);
