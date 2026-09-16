using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.Security;

public sealed record SecuritySettings(
    int IdleTimeoutMinutes = 15,                   // Range: [1, 1440]
    int MaxFailedPinAttempts = 5,                  // Range: [3, 10]
    int PinLockoutMinutes = 15,                    // Range: [1, 1440]
    decimal MaxCashierDiscountPercentage = 10.00m, // Range: [0.00, 100.00]
    bool RequireSupervisorForRefund = true,
    bool RequireSupervisorForVoid = true,
    bool RequireSupervisorForPriceOverride = true)
{
    public void Validate()
    {
        if (IdleTimeoutMinutes < 1 || IdleTimeoutMinutes > 1440)
            throw new DomainException("IdleTimeoutMinutes must be between 1 and 1440.", "INVALID_SECURITY_SETTING");

        if (MaxFailedPinAttempts < 3 || MaxFailedPinAttempts > 10)
            throw new DomainException("MaxFailedPinAttempts must be between 3 and 10.", "INVALID_SECURITY_SETTING");

        if (PinLockoutMinutes < 1 || PinLockoutMinutes > 1440)
            throw new DomainException("PinLockoutMinutes must be between 1 and 1440.", "INVALID_SECURITY_SETTING");

        if (MaxCashierDiscountPercentage < 0.00m || MaxCashierDiscountPercentage > 100.00m)
            throw new DomainException("MaxCashierDiscountPercentage must be between 0.00 and 100.00.", "INVALID_SECURITY_SETTING");
    }
}

public sealed record FailsafeActiveMarker(
    // IMMUTABLE EPISODE IDENTITY: Never mutated for the entire lifetime of the failsafe episode
    Guid FailsafeInstanceId,
    DateTime EntryTimestampUtc,
    string EntryReason,
    // MUTABLE RECOVERY STATE: Represents active clear transaction; updated crash-safely
    PendingClearOperation? PendingClear);

public sealed record PendingClearOperation(
    Guid ClearOperationId,
    DateTime InitiatedTimestampUtc,
    Guid OwnerUserId,
    string OwnerUsername,
    string TargetSettingsSha256,
    string StagedFilePath);
