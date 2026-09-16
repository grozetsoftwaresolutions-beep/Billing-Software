using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Audit;

public sealed record AuditLogDto(
    long Id,
    DateTime CreatedAtUtc,
    Guid? UserId,
    string? UserName,
    Guid? TerminalId,
    string Action,
    string EntityName,
    string? EntityId,
    string? Details,
    string? Reason,
    string? BeforeJson,
    string? AfterJson,
    string? IpAddress,
    string? CorrelationId,
    Guid? FailsafeInstanceId,
    Guid? ClearOperationId,
    string? TargetSettingsSha256,
    bool Success);

public sealed record AuditLogQueryFilter(
    DateTime? FromDateUtc = null,
    DateTime? ToDateUtc = null,
    Guid? UserId = null,
    string? UserName = null,
    Guid? TerminalId = null,
    string? Action = null,
    string? EntityName = null,
    bool? Success = null,
    int PageNumber = 1,
    int PageSize = 50);

public sealed record UserAuditSnapshot(
    Guid UserId,
    string Username,
    string FullName,
    RoleType Role,
    bool IsActive);

public sealed record SaleInvoiceAuditSnapshot(
    string InvoiceNumber,
    SaleStatus Status,
    decimal GrossSubtotal,
    decimal TotalTax,
    decimal FinalPayable,
    int ReprintCount,
    IReadOnlyList<SaleInvoiceItemAuditSnapshot> Items);

public sealed record SaleInvoiceItemAuditSnapshot(
    Guid ProductId,
    string ProductSku,
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record SecuritySettingsAuditSnapshot(
    int IdleTimeoutMinutes,
    int MaxFailedPinAttempts,
    int PinLockoutMinutes,
    decimal MaxCashierDiscountPercentage,
    bool RequireSupervisorForRefund,
    bool RequireSupervisorForVoid,
    bool RequireSupervisorForPriceOverride);
