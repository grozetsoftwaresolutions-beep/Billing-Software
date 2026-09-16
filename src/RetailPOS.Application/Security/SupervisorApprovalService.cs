using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Security;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Security;

public sealed class SupervisorApprovalService : ISupervisorApprovalService
{
    private readonly ISupervisorAuthorizationCore _authCore;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    private readonly ConcurrentDictionary<string, SupervisorApprovalRecord> _tokens = new(StringComparer.Ordinal);
    private readonly object _capacityLock = new();
    private const int MaxCapacity = 1000;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(120);

    public int ActiveTokenCount => _tokens.Count;

    public SupervisorApprovalService(
        ISupervisorAuthorizationCore authCore,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _authCore = authCore;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<SupervisorApprovalResponse> IssueApprovalTokenAsync(
        SupervisorApprovalRequest request,
        Guid requestingUserId,
        string requestingSessionIdentityHash,
        Guid terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Authorize supervisor credentials
        var authResult = await _authCore.AuthorizeSupervisorCredentialsAsync(
            request.Username,
            request.Pin,
            request.Password,
            request.RequiredPermission,
            terminalId,
            ct);

        if (!authResult.IsAuthorized)
        {
            throw new DomainException(authResult.ErrorMessage ?? "Supervisor credentials rejected.", "SUPERVISOR_AUTHORIZATION_FAILED");
        }

        // 2. Generate cryptographically strong random bearer token
        var rawTokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToHexString(rawTokenBytes).ToLowerInvariant();
        var tokenHash = ComputeSha256(rawToken);

        var now = DateTime.UtcNow;
        var expiresAt = now.Add(TokenLifetime);
        var tokenId = Guid.NewGuid();

        var record = new SupervisorApprovalRecord(
            tokenId,
            tokenHash,
            authResult.SupervisorUserId!.Value,
            authResult.SupervisorUsername!,
            authResult.SupervisorRole!.Value,
            requestingUserId,
            requestingSessionIdentityHash,
            terminalId,
            request.RequiredPermission,
            request.EntityType,
            request.EntityId,
            request.RequestNonce,
            request.Reason?.Trim() ?? string.Empty,
            now,
            expiresAt);

        // 3. Concurrency-safe atomic capacity reservation
        lock (_capacityLock)
        {
            // Purge expired tokens
            var currentNow = DateTime.UtcNow;
            foreach (var kvp in _tokens)
            {
                if (currentNow > kvp.Value.ExpiresAtUtc)
                {
                    _tokens.TryRemove(kvp.Key, out _);
                }
            }

            if (_tokens.Count >= MaxCapacity)
            {
                throw new DomainException("Active supervisor approval token capacity exceeded. Retry later.", "APPROVAL_TOKEN_CAPACITY_EXCEEDED");
            }

            _tokens[tokenHash] = record;
        }

        // 4. Audit issuance
        var audit = new AuditLog(
            0,
            "SUPERVISOR_APPROVAL_GRANTED",
            request.EntityType,
            request.EntityId ?? request.RequestNonce,
            authResult.SupervisorUserId.Value,
            terminalId,
            $"Supervisor approval token granted for {request.RequiredPermission}",
            success: true,
            userName: authResult.SupervisorUsername!,
            reason: request.Reason);
        await _auditRepo.RecordAsync(audit, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return new SupervisorApprovalResponse(
            rawToken,
            tokenId,
            expiresAt,
            authResult.SupervisorUserId.Value,
            authResult.SupervisorUsername ?? string.Empty,
            request.RequiredPermission,
            request.EntityType,
            request.EntityId,
            request.RequestNonce);
    }

    public async Task<ApprovalValidationResult> ValidateAndConsumeTokenAsync(
        string rawToken,
        Guid requestingUserId,
        string sessionIdentityHash,
        Guid terminalId,
        Permission requiredPermission,
        string entityType,
        string? entityId,
        string? requestNonce,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return ApprovalValidationResult.Failed("APPROVAL_TOKEN_REQUIRED");

        var tokenHash = ComputeSha256(rawToken.Trim());

        if (!_tokens.TryGetValue(tokenHash, out var record))
        {
            return ApprovalValidationResult.Failed("APPROVAL_TOKEN_INVALID_OR_USED");
        }

        // Expiration boundary check (inclusive at boundary: UtcNow <= ExpiresAtUtc is valid)
        if (DateTime.UtcNow > record.ExpiresAtUtc)
        {
            _tokens.TryRemove(tokenHash, out _);
            return ApprovalValidationResult.Failed("APPROVAL_TOKEN_EXPIRED");
        }

        // Scope bindings
        if (record.TerminalId != terminalId)
            return ApprovalValidationResult.Failed("APPROVAL_TERMINAL_MISMATCH");

        if (record.RequestingUserId != requestingUserId)
            return ApprovalValidationResult.Failed("APPROVAL_USER_MISMATCH");

        if (!string.Equals(record.RequestingSessionIdentityHash, sessionIdentityHash, StringComparison.Ordinal))
            return ApprovalValidationResult.Failed("APPROVAL_SESSION_MISMATCH");

        if (record.RequiredPermission != requiredPermission)
            return ApprovalValidationResult.Failed("APPROVAL_PERMISSION_MISMATCH");

        if (!string.Equals(record.EntityType, entityType, StringComparison.OrdinalIgnoreCase))
            return ApprovalValidationResult.Failed("APPROVAL_ENTITY_TYPE_MISMATCH");

        // STRICT SYMMETRIC ENTITY ID CHECK:
        // If either side has an EntityId, both must have it and match character-for-character.
        if (!string.Equals(record.EntityId, entityId, StringComparison.OrdinalIgnoreCase))
        {
            return ApprovalValidationResult.Failed("APPROVAL_ENTITY_ID_MISMATCH");
        }

        // STRICT SYMMETRIC REQUEST NONCE CHECK:
        // If either side has a RequestNonce, both must have it and match character-for-character.
        if (!string.Equals(record.RequestNonce, requestNonce, StringComparison.OrdinalIgnoreCase))
        {
            return ApprovalValidationResult.Failed("APPROVAL_NONCE_MISMATCH");
        }

        // Atomic compare-and-consume CAS removal
        var kvp = new KeyValuePair<string, SupervisorApprovalRecord>(tokenHash, record);
        if (!_tokens.TryRemove(kvp))
        {
            return ApprovalValidationResult.Failed("APPROVAL_TOKEN_ALREADY_CONSUMED");
        }

        // Audit consumption
        var audit = new AuditLog(
            0,
            "SUPERVISOR_TOKEN_CONSUMED",
            entityType,
            entityId ?? requestNonce,
            record.SupervisorUserId,
            terminalId,
            $"Supervisor approval token consumed for {requiredPermission}",
            success: true,
            userName: record.SupervisorUsername,
            reason: record.AuditReason);
        await _auditRepo.RecordAsync(audit, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return ApprovalValidationResult.Success(record);
    }

    public async Task<SupervisorAuthorizationResult> AuthorizeInlineOrTokenAsync(
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
        CancellationToken ct = default)
    {
        // Path A: Token presented
        if (!string.IsNullOrWhiteSpace(rawToken))
        {
            var valResult = await ValidateAndConsumeTokenAsync(
                rawToken,
                requestingUserId,
                sessionIdentityHash,
                terminalId,
                requiredPermission,
                entityType,
                entityId,
                requestNonce,
                ct);

            if (!valResult.IsValid)
            {
                return SupervisorAuthorizationResult.Failed(valResult.ErrorMessage ?? "TOKEN_VALIDATION_FAILED");
            }

            return SupervisorAuthorizationResult.Success(
                valResult.Record!.SupervisorUserId,
                valResult.Record.SupervisorUsername,
                valResult.Record.SupervisorRole);
        }

        // Path B: Inline credentials presented
        if (!string.IsNullOrWhiteSpace(supervisorUsername) &&
            (!string.IsNullOrWhiteSpace(supervisorPin) || !string.IsNullOrWhiteSpace(supervisorPassword)))
        {
            var authResult = await _authCore.AuthorizeSupervisorCredentialsAsync(
                supervisorUsername,
                supervisorPin,
                supervisorPassword,
                requiredPermission,
                terminalId,
                ct);

            if (!authResult.IsAuthorized)
            {
                return authResult;
            }

            // Audit inline approval
            var audit = new AuditLog(
                0,
                "SUPERVISOR_APPROVAL_GRANTED",
                entityType,
                entityId ?? requestNonce,
                authResult.SupervisorUserId!.Value,
                terminalId,
                $"Inline supervisor approval granted for {requiredPermission}",
                success: true,
                userName: authResult.SupervisorUsername,
                reason: reason);
            await _auditRepo.RecordAsync(audit, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return authResult;
        }

        return SupervisorAuthorizationResult.Failed("SUPERVISOR_APPROVAL_REQUIRED");
    }

    public void InvalidateSessionTokens(string sessionIdentityHash, Guid? terminalId = null)
    {
        foreach (var kvp in _tokens)
        {
            if (string.Equals(kvp.Value.RequestingSessionIdentityHash, sessionIdentityHash, StringComparison.Ordinal) ||
                (terminalId.HasValue && kvp.Value.TerminalId == terminalId.Value))
            {
                _tokens.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
