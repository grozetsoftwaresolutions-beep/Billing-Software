namespace RetailPOS.Contracts.Security;

using System;

public sealed record RotateRecoveryKeyResponseDto(
    string StoreMasterRecoveryKey,
    string ConfirmationToken,
    DateTime ExpiresAtUtc);

public sealed record ConfirmRecoveryKeyRotationRequestDto(
    string ConfirmationToken);

public sealed record ConfirmRecoveryKeyRotationResponseDto(
    string Message,
    DateTime CommittedAtUtc);

public sealed record RotationStatusResponseDto(
    string? RotationState,
    bool HasPendingRotation,
    DateTime? ExpiresAtUtc,
    string? InitiatedByUserId);
