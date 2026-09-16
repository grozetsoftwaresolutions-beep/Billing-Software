namespace RetailPOS.Contracts.Security;

public sealed record SecuritySettingsDto(
    int IdleTimeoutMinutes = 15,
    int MaxFailedPinAttempts = 5,
    int PinLockoutMinutes = 15,
    decimal MaxCashierDiscountPercentage = 10.00m,
    bool RequireSupervisorForRefund = true,
    bool RequireSupervisorForVoid = true,
    bool RequireSupervisorForPriceOverride = true);

public sealed record UpdateSecuritySettingsRequest(
    int IdleTimeoutMinutes,
    int MaxFailedPinAttempts,
    int PinLockoutMinutes,
    decimal MaxCashierDiscountPercentage,
    bool RequireSupervisorForRefund,
    bool RequireSupervisorForVoid,
    bool RequireSupervisorForPriceOverride);

public sealed record FailsafeStatusResponse(
    bool IsFailsafeActive,
    Guid? FailsafeInstanceId,
    DateTime? EntryTimestampUtc,
    string? EntryReason,
    bool HasPendingClear,
    Guid? PendingClearOperationId);

public sealed record ClearFailsafeRequest(
    SecuritySettingsDto NewSettings,
    string Reason);
