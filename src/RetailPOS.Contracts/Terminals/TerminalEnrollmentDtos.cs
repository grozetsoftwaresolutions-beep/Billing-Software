using RetailPOS.Contracts.Discovery;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Contracts.Terminals;

public sealed record EnrollmentTokenRequestDto(
    EnrollmentTokenPurpose Purpose,
    Guid? TerminalId = null,
    int TtlMinutes = 15);

public sealed record EnrollmentTokenResponseDto(
    string Token,
    EnrollmentTokenPurpose Purpose,
    Guid? BoundTerminalId,
    DateTimeOffset ExpiresAtUtc);

public sealed record TerminalEnrollRequestDto(
    string PairingToken,
    string TerminalCode,
    string TerminalName,
    string MachineName,
    string? ClientIpAddress = null);

public sealed record TerminalRepairRequestDto(
    Guid TerminalId,
    string RepairToken,
    string? ClientIpAddress = null);

public sealed record TerminalEnrollResponseDto(
    Guid TerminalId,
    string TerminalCode,
    string TerminalAuthToken,
    ServerIdentityInfo ServerIdentity);

public sealed record TerminalHeartbeatDto(
    Guid TerminalId,
    string MachineName,
    string? ClientIpAddress,
    DateTimeOffset ClientTimestampUtc);

public sealed record TerminalHeartbeatResponseDto(
    Guid TerminalId,
    TerminalStatus Status,
    CounterConnectivityStatus Connectivity,
    DateTimeOffset ServerTimestampUtc);

public sealed record TerminalStatusUpdateDto(
    TerminalStatus Status);

public sealed record TerminalDetailDto(
    Guid Id,
    string TerminalCode,
    string TerminalName,
    string? ReceiptPrinterName,
    TerminalStatus Status,
    bool IsActive,
    string? MachineName,
    string? ClientIpAddress,
    DateTime? LastHeartbeatUtc,
    CounterConnectivityStatus Connectivity);

public sealed record RePinningTokenRequestDto(
    Guid TerminalId,
    int TtlMinutes = 15);

public sealed record RePinningTokenResponseDto(
    string Token,
    Guid TerminalId,
    string BoundServerId,
    string BoundServerFingerprint,
    DateTimeOffset ExpiresAtUtc);

public sealed record RePinningRequestDto(
    Guid TerminalId,
    string RePinningToken,
    ServerChallengeResponseDto VerifiedChallenge);

public sealed record RePinningResponseDto(
    Guid TerminalId,
    string NewTerminalAuthToken,
    ServerIdentityInfo NewServerIdentity);

public sealed record ProvisionReplacementIdentityRequestDto(
    string StoreMasterRecoveryKey);

public sealed record ProvisionReplacementIdentityResponseDto(
    string ServerId,
    Guid StoreId,
    string ServerFingerprint,
    string HumanFingerprint,
    DateTimeOffset CreatedAtUtc);
