using System.Security.Cryptography;
using System.Text;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Discovery;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Application.Services;

public sealed class TerminalEnrollmentService : ITerminalEnrollmentService
{
    private readonly ITerminalRepository _terminalRepository;
    private readonly ITerminalEnrollmentTokenRepository _tokenRepository;
    private readonly IServerIdentityService _serverIdentityService;
    private readonly ITerminalHeartbeatTracker _heartbeatTracker;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IUnitOfWork _unitOfWork;

    public TerminalEnrollmentService(
        ITerminalRepository terminalRepository,
        ITerminalEnrollmentTokenRepository tokenRepository,
        IServerIdentityService serverIdentityService,
        ITerminalHeartbeatTracker heartbeatTracker,
        IAuditLogRepository auditLogRepository,
        IUnitOfWork unitOfWork)
    {
        _terminalRepository = terminalRepository;
        _tokenRepository = tokenRepository;
        _serverIdentityService = serverIdentityService;
        _heartbeatTracker = heartbeatTracker;
        _auditLogRepository = auditLogRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<EnrollmentTokenResponseDto> GenerateEnrollmentTokenAsync(EnrollmentTokenRequestDto request, string currentUserId, CancellationToken ct = default)
    {
        string rawToken = GenerateSecureTokenString();
        string tokenHash = ComputeTokenHash(rawToken);
        var expiresAt = DateTime.UtcNow.AddMinutes(request.TtlMinutes <= 0 ? 15 : request.TtlMinutes);

        var token = new TerminalEnrollmentToken(
            Guid.NewGuid(),
            tokenHash,
            request.Purpose,
            request.TerminalId,
            null,
            null,
            expiresAt,
            currentUserId);

        await _tokenRepository.AddAsync(token, ct);
        await _auditLogRepository.RecordAsync(
            "TerminalEnrollmentToken",
            "TOKEN_GENERATED",
            currentUserId,
            $"Generated enrollment token for purpose {request.Purpose}. BoundTerminalId: {request.TerminalId}",
            ct);

        await _unitOfWork.SaveChangesAsync(ct);

        return new EnrollmentTokenResponseDto(rawToken, request.Purpose, request.TerminalId, expiresAt);
    }

    public async Task<TerminalEnrollResponseDto> EnrollTerminalAsync(TerminalEnrollRequestDto request, CancellationToken ct = default)
    {
        string tokenHash = ComputeTokenHash(request.PairingToken);
        var token = await _tokenRepository.GetByHashAsync(tokenHash, ct);
        if (token is null)
            throw new DomainException("Invalid enrollment token.", "INVALID_ENROLLMENT_TOKEN");

        if (token.TokenPurpose != EnrollmentTokenPurpose.NewEnrollment)
            throw new DomainException("Token purpose is not for new enrollment.", "INVALID_TOKEN_PURPOSE");

        var now = DateTime.UtcNow;
        token.Consume(now);

        var existingTerminal = await _terminalRepository.GetByCodeAsync(request.TerminalCode, ct);
        Terminal terminal;

        string rawAuthToken = GenerateSecureTokenString();
        string authTokenHash = ComputeTokenHash(rawAuthToken);

        if (existingTerminal is not null)
        {
            if (existingTerminal.Status == TerminalStatus.Decommissioned)
                throw new DomainException("Cannot enroll decommissioned terminal.", "TERMINAL_DECOMMISSIONED");

            terminal = existingTerminal;
            terminal.Enroll(authTokenHash, request.MachineName, request.ClientIpAddress);
        }
        else
        {
            terminal = new Terminal(
                Guid.NewGuid(),
                request.TerminalCode,
                request.TerminalName,
                null,
                TerminalStatus.Active,
                authTokenHash,
                request.MachineName,
                request.ClientIpAddress);

            await _terminalRepository.AddAsync(terminal, ct);
        }

        await _auditLogRepository.RecordAsync(
            "Terminal",
            "TERMINAL_ENROLLED",
            "SYSTEM",
            $"Enrolled terminal {terminal.TerminalCode} ({terminal.Id}) from host {request.MachineName}",
            ct);

        await _unitOfWork.SaveChangesAsync(ct);

        var serverIdentity = await _serverIdentityService.GetServerIdentityAsync(ct)
            ?? throw new DomainException("Server identity not available.", "SERVER_IDENTITY_NOT_AVAILABLE");

        return new TerminalEnrollResponseDto(terminal.Id, terminal.TerminalCode, rawAuthToken, serverIdentity);
    }

    public async Task<TerminalEnrollResponseDto> RepairTerminalAsync(Guid terminalId, string repairToken, string? clientIpAddress, CancellationToken ct = default)
    {
        var terminal = await _terminalRepository.GetByIdAsync(terminalId, ct);
        if (terminal is null)
            throw new DomainException("Terminal not found.", "TERMINAL_NOT_FOUND");

        if (terminal.Status == TerminalStatus.Decommissioned)
            throw new DomainException("Terminal is decommissioned.", "TERMINAL_DECOMMISSIONED");

        string tokenHash = ComputeTokenHash(repairToken);
        var token = await _tokenRepository.GetByHashAsync(tokenHash, ct);
        if (token is null)
            throw new DomainException("Invalid repair token.", "INVALID_ENROLLMENT_TOKEN");

        if (token.TokenPurpose != EnrollmentTokenPurpose.ExistingTerminalRepair)
            throw new DomainException("Token purpose is not for terminal repair.", "INVALID_TOKEN_PURPOSE");

        if (token.BoundTerminalId != terminalId)
            throw new DomainException("Repair token is bound to a different terminal.", "TOKEN_BINDING_MISMATCH");

        token.Consume(DateTime.UtcNow);

        string rawAuthToken = GenerateSecureTokenString();
        string authTokenHash = ComputeTokenHash(rawAuthToken);

        terminal.RotatePairingToken(authTokenHash);
        if (terminal.Status == TerminalStatus.Inactive)
            terminal.Activate();

        await _auditLogRepository.RecordAsync(
            "Terminal",
            "TERMINAL_REPAIRED",
            "SYSTEM",
            $"Repaired terminal {terminal.TerminalCode} ({terminal.Id})",
            ct);

        await _unitOfWork.SaveChangesAsync(ct);

        var serverIdentity = await _serverIdentityService.GetServerIdentityAsync(ct)
            ?? throw new DomainException("Server identity not available.", "SERVER_IDENTITY_NOT_AVAILABLE");

        return new TerminalEnrollResponseDto(terminal.Id, terminal.TerminalCode, rawAuthToken, serverIdentity);
    }

    public async Task<RePinningTokenResponseDto> GenerateRePinningTokenAsync(RePinningTokenRequestDto request, string currentUserId, CancellationToken ct = default)
    {
        var terminal = await _terminalRepository.GetByIdAsync(request.TerminalId, ct);
        if (terminal is null)
            throw new DomainException("Terminal not found.", "TERMINAL_NOT_FOUND");

        if (terminal.Status == TerminalStatus.Decommissioned)
            throw new DomainException("Terminal is decommissioned.", "TERMINAL_DECOMMISSIONED");

        var serverIdentity = await _serverIdentityService.GetServerIdentityAsync(ct)
            ?? throw new DomainException("Server identity not available.", "SERVER_IDENTITY_NOT_AVAILABLE");

        string rawToken = GenerateSecureTokenString();
        string tokenHash = ComputeTokenHash(rawToken);
        var expiresAt = DateTime.UtcNow.AddMinutes(request.TtlMinutes <= 0 ? 15 : request.TtlMinutes);

        var token = new TerminalEnrollmentToken(
            Guid.NewGuid(),
            tokenHash,
            EnrollmentTokenPurpose.ServerIdentityRePinning,
            request.TerminalId,
            serverIdentity.ServerId,
            serverIdentity.ServerFingerprint,
            expiresAt,
            currentUserId);

        await _tokenRepository.AddAsync(token, ct);
        await _auditLogRepository.RecordAsync(
            "TerminalEnrollmentToken",
            "REPINNING_TOKEN_GENERATED",
            currentUserId,
            $"Generated re-pinning token for terminal {terminal.TerminalCode} ({terminal.Id}) bound to ServerId {serverIdentity.ServerId}",
            ct);

        await _unitOfWork.SaveChangesAsync(ct);

        return new RePinningTokenResponseDto(rawToken, terminal.Id, serverIdentity.ServerId, serverIdentity.ServerFingerprint, expiresAt);
    }

    public async Task<RePinningResponseDto> RePinTerminalAsync(RePinningRequestDto request, CancellationToken ct = default)
    {
        var terminal = await _terminalRepository.GetByIdAsync(request.TerminalId, ct);
        if (terminal is null)
            throw new DomainException("Terminal not found.", "TERMINAL_NOT_FOUND");

        if (terminal.Status == TerminalStatus.Decommissioned)
            throw new DomainException("Terminal is decommissioned.", "TERMINAL_DECOMMISSIONED");

        string tokenHash = ComputeTokenHash(request.RePinningToken);
        var token = await _tokenRepository.GetByHashAsync(tokenHash, ct);
        if (token is null)
            throw new DomainException("Invalid re-pinning token.", "INVALID_REPINNING_TOKEN");

        if (token.TokenPurpose != EnrollmentTokenPurpose.ServerIdentityRePinning)
            throw new DomainException("Token purpose is not for server re-pinning.", "INVALID_TOKEN_PURPOSE");

        if (token.BoundTerminalId != request.TerminalId)
            throw new DomainException("Re-pinning token is bound to a different terminal.", "TOKEN_BINDING_MISMATCH");

        var serverIdentity = await _serverIdentityService.GetServerIdentityAsync(ct)
            ?? throw new DomainException("Server identity not available.", "SERVER_IDENTITY_NOT_AVAILABLE");

        if (!string.Equals(token.BoundServerId, serverIdentity.ServerId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(token.BoundServerFingerprint, serverIdentity.ServerFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("Re-pinning token is not bound to current server identity.", "SERVER_IDENTITY_MISMATCH");
        }

        if (!string.Equals(request.VerifiedChallenge.Purpose, ChallengePurposes.ServerIdentityRePinning, StringComparison.Ordinal))
            throw new DomainException("Challenge purpose must be ServerIdentityRePinning.", "INVALID_CHALLENGE_PURPOSE");

        token.Consume(DateTime.UtcNow);

        string rawAuthToken = GenerateSecureTokenString();
        string authTokenHash = ComputeTokenHash(rawAuthToken);

        terminal.RotatePairingToken(authTokenHash);
        if (terminal.Status == TerminalStatus.Inactive)
            terminal.Activate();

        await _auditLogRepository.RecordAsync(
            "Terminal",
            "TERMINAL_REPINNED",
            "SYSTEM",
            $"Re-pinned terminal {terminal.TerminalCode} ({terminal.Id}) to replacement server {serverIdentity.ServerId}",
            ct);

        await _unitOfWork.SaveChangesAsync(ct);

        return new RePinningResponseDto(terminal.Id, rawAuthToken, serverIdentity);
    }

    public async Task DecommissionTerminalAsync(Guid terminalId, string currentUserId, CancellationToken ct = default)
    {
        var terminal = await _terminalRepository.GetByIdAsync(terminalId, ct);
        if (terminal is null)
            throw new DomainException("Terminal not found.", "TERMINAL_NOT_FOUND");

        if (terminal.Status == TerminalStatus.Decommissioned)
            throw new DomainException("Terminal is already decommissioned.", "TERMINAL_ALREADY_DECOMMISSIONED");

        terminal.Decommission();
        await _auditLogRepository.RecordAsync(
            "Terminal",
            "TERMINAL_DECOMMISSIONED",
            currentUserId,
            $"Permanently decommissioned terminal {terminal.TerminalCode} ({terminal.Id})",
            ct);

        await _unitOfWork.SaveChangesAsync(ct);
    }

    public async Task UpdateTerminalStatusAsync(Guid terminalId, TerminalStatus status, string currentUserId, CancellationToken ct = default)
    {
        var terminal = await _terminalRepository.GetByIdAsync(terminalId, ct);
        if (terminal is null)
            throw new DomainException("Terminal not found.", "TERMINAL_NOT_FOUND");

        if (terminal.Status == TerminalStatus.Decommissioned)
            throw new DomainException("Cannot modify decommissioned terminal.", "TERMINAL_DECOMMISSIONED");

        if (status == TerminalStatus.Decommissioned)
        {
            terminal.Decommission();
        }
        else if (status == TerminalStatus.Active)
        {
            terminal.Activate();
        }
        else if (status == TerminalStatus.Inactive)
        {
            terminal.Deactivate();
        }

        await _auditLogRepository.RecordAsync(
            "Terminal",
            "TERMINAL_STATUS_UPDATED",
            currentUserId,
            $"Updated terminal {terminal.TerminalCode} ({terminal.Id}) status to {status}",
            ct);

        await _unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TerminalDetailDto>> GetAllTerminalDetailsAsync(CancellationToken ct = default)
    {
        var terminals = await _terminalRepository.GetAllAsync(ct);
        var dtos = new List<TerminalDetailDto>(terminals.Count);

        foreach (var t in terminals)
        {
            var connectivity = _heartbeatTracker.GetConnectivityStatus(t.Id, t.LastHeartbeatUtc);
            dtos.Add(new TerminalDetailDto(
                t.Id,
                t.TerminalCode,
                t.TerminalName,
                t.ReceiptPrinterName,
                t.Status,
                t.IsActive,
                t.MachineName,
                t.ClientIpAddress,
                t.LastHeartbeatUtc,
                connectivity));
        }

        return dtos.AsReadOnly();
    }

    public async Task<TerminalDetailDto?> GetTerminalDetailAsync(Guid terminalId, CancellationToken ct = default)
    {
        var t = await _terminalRepository.GetByIdAsync(terminalId, ct);
        if (t is null) return null;

        var connectivity = _heartbeatTracker.GetConnectivityStatus(t.Id, t.LastHeartbeatUtc);
        return new TerminalDetailDto(
            t.Id,
            t.TerminalCode,
            t.TerminalName,
            t.ReceiptPrinterName,
            t.Status,
            t.IsActive,
            t.MachineName,
            t.ClientIpAddress,
            t.LastHeartbeatUtc,
            connectivity);
    }

    private static string GenerateSecureTokenString()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToHexString(bytes); // 48 uppercase hex characters
    }

    private static string ComputeTokenHash(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToHexString(hash); // 64 uppercase hex characters
    }
}
