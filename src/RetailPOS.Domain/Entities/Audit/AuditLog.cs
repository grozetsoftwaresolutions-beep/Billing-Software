using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.Entities.Audit;

public sealed class AuditLog : BaseEntity<long>
{
    public Guid? UserId { get; private set; }
    public string? UserName { get; private set; }
    public Guid? TerminalId { get; private set; }
    public string Action { get; private set; } = null!;
    public string EntityName { get; private set; } = null!;
    public string? EntityId { get; private set; }
    public string? Details { get; private set; }
    public string? Reason { get; private set; }
    public string? BeforeJson { get; private set; }
    public string? AfterJson { get; private set; }
    public string? IpAddress { get; private set; }
    public string? CorrelationId { get; private set; }

    // Failsafe recovery identities
    public Guid? FailsafeInstanceId { get; private set; }
    public Guid? ClearOperationId { get; private set; }
    public string? TargetSettingsSha256 { get; private set; }

    public bool Success { get; private set; }

    private AuditLog() { }

    public AuditLog(
        long id,
        string action,
        string entityName,
        string? entityId = null,
        Guid? userId = null,
        Guid? terminalId = null,
        string? details = null,
        bool success = true,
        string? userName = null,
        string? reason = null,
        string? beforeJson = null,
        string? afterJson = null,
        string? ipAddress = null,
        string? correlationId = null,
        Guid? failsafeInstanceId = null,
        Guid? clearOperationId = null,
        string? targetSettingsSha256 = null)
    {
        Id = id;
        Action = action;
        EntityName = entityName;
        EntityId = entityId;
        UserId = userId;
        TerminalId = terminalId;
        Details = details;
        Success = success;
        UserName = userName;
        Reason = reason;
        BeforeJson = beforeJson;
        AfterJson = afterJson;
        IpAddress = ipAddress;
        CorrelationId = correlationId;
        FailsafeInstanceId = failsafeInstanceId;
        ClearOperationId = clearOperationId;
        TargetSettingsSha256 = targetSettingsSha256;
    }
}
