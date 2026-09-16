using RetailPOS.Contracts.Security;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Security;

public sealed record SupervisorApprovalRecord(
    Guid ApprovalTokenId,
    string TokenHash,
    Guid SupervisorUserId,
    string SupervisorUsername,
    RoleType SupervisorRole,
    Guid RequestingUserId,
    string RequestingSessionIdentityHash,
    Guid TerminalId,
    Permission RequiredPermission,
    string EntityType,
    string? EntityId,
    string? RequestNonce,
    string AuditReason,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc);

public sealed record ApprovalValidationResult(
    bool IsValid,
    string? ErrorMessage = null,
    SupervisorApprovalRecord? Record = null)
{
    public static ApprovalValidationResult Success(SupervisorApprovalRecord record) =>
        new(true, null, record);
    public static ApprovalValidationResult Failed(string error) =>
        new(false, error, null);
}

public interface ISupervisorApprovalService
{
    Task<SupervisorApprovalResponse> IssueApprovalTokenAsync(
        SupervisorApprovalRequest request,
        Guid requestingUserId,
        string requestingSessionIdentityHash,
        Guid terminalId,
        CancellationToken ct = default);

    Task<ApprovalValidationResult> ValidateAndConsumeTokenAsync(
        string rawToken,
        Guid requestingUserId,
        string sessionIdentityHash,
        Guid terminalId,
        Permission requiredPermission,
        string entityType,
        string? entityId,
        string? requestNonce,
        CancellationToken ct = default);

    Task<SupervisorAuthorizationResult> AuthorizeInlineOrTokenAsync(
        string? rawToken,
        string? supervisorUsername,
        string? supervisorPin,
        string? supervisorPassword,
        Guid requestingUserId,
        string sessionIdentityHash,
        Guid terminalId,
        Permission requiredPermission,
        string entityType,
        string? entityId,
        string? requestNonce,
        string reason,
        CancellationToken ct = default);

    void InvalidateSessionTokens(string sessionIdentityHash, Guid? terminalId = null);
    int ActiveTokenCount { get; }
}
