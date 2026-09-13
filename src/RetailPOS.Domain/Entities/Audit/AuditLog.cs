using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.Entities.Audit;

/// <summary>
/// Immutable audit log for security and business-critical operations.
/// Append-only, never deleted.
/// </summary>
public sealed class AuditLog : BaseEntity<long>
{
    public Guid? UserId { get; private set; }
    public Guid? TerminalId { get; private set; }
    public string Action { get; private set; }
    public string EntityName { get; private set; }
    public string? EntityId { get; private set; }
    public string? Details { get; private set; }
    public bool Success { get; private set; }

    public AuditLog(
        long id,
        string action,
        string entityName,
        string? entityId = null,
        Guid? userId = null,
        Guid? terminalId = null,
        string? details = null,
        bool success = true)
    {
        Id = id;
        Action = action;
        EntityName = entityName;
        EntityId = entityId;
        UserId = userId;
        TerminalId = terminalId;
        Details = details;
        Success = success;
    }
}
