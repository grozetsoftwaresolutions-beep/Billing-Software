using RetailPOS.Contracts.Audit;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Application.Services;

public interface IAuditService
{
    Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQueryFilter filter, CancellationToken ct = default);
    Task<string> ExportCsvAsync(AuditLogQueryFilter filter, CancellationToken ct = default);
    Task RecordAsync(
        string action,
        string entityName,
        string? entityId,
        Guid? userId,
        string? userName,
        Guid? terminalId,
        string? details,
        string? reason,
        string? beforeJson,
        string? afterJson,
        string? ipAddress,
        string? correlationId,
        bool success = true,
        Guid? failsafeInstanceId = null,
        Guid? clearOperationId = null,
        string? targetSettingsSha256 = null,
        CancellationToken ct = default);
}
