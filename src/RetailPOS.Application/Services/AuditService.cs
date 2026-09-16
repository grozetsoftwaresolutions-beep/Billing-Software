using System.Text;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security;
using RetailPOS.Contracts.Audit;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Audit;

namespace RetailPOS.Application.Services;

public sealed class AuditService : IAuditService
{
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public AuditService(IAuditLogRepository auditRepo, IUnitOfWork unitOfWork)
    {
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQueryFilter filter, CancellationToken ct = default)
    {
        filter ??= new AuditLogQueryFilter();
        var paged = await _auditRepo.GetAuditLogsAsync(filter, ct);

        var dtos = paged.Items.Select(MapToDto).ToList();
        return new PagedResult<AuditLogDto>(dtos, paged.TotalCount, paged.Page, paged.PageSize);
    }

    public async Task<string> ExportCsvAsync(AuditLogQueryFilter filter, CancellationToken ct = default)
    {
        filter ??= new AuditLogQueryFilter();
        var logs = await _auditRepo.GetAllForExportAsync(filter, ct);

        var sb = new StringBuilder();
        // RFC 4180 CSV Header
        sb.AppendLine("Id,CreatedAtUtc,UserId,UserName,TerminalId,Action,EntityName,EntityId,Details,Reason,Success,IpAddress,CorrelationId");

        foreach (var log in logs)
        {
            sb.Append(log.Id).Append(',');
            sb.Append(EscapeCsv(log.CreatedAtUtc.ToString("o"))).Append(',');
            sb.Append(EscapeCsv(log.UserId?.ToString())).Append(',');
            sb.Append(EscapeCsv(log.UserName)).Append(',');
            sb.Append(EscapeCsv(log.TerminalId?.ToString())).Append(',');
            sb.Append(EscapeCsv(log.Action)).Append(',');
            sb.Append(EscapeCsv(log.EntityName)).Append(',');
            sb.Append(EscapeCsv(log.EntityId)).Append(',');
            sb.Append(EscapeCsv(log.Details)).Append(',');
            sb.Append(EscapeCsv(log.Reason)).Append(',');
            sb.Append(log.Success ? "true" : "false").Append(',');
            sb.Append(EscapeCsv(log.IpAddress)).Append(',');
            sb.Append(EscapeCsv(log.CorrelationId)).AppendLine();
        }

        return sb.ToString();
    }

    public async Task RecordAsync(
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
        CancellationToken ct = default)
    {
        if (reason != null && reason.Length > 500)
            throw new DomainException("Audit reason exceeds maximum length of 500 characters.", "REASON_EXCEEDS_MAX_LENGTH");

        var sanitizedBefore = AuditJsonSanitizer.Sanitize(beforeJson);
        var sanitizedAfter = AuditJsonSanitizer.Sanitize(afterJson);

        if (sanitizedBefore != null && sanitizedBefore.Length > 4000)
            throw new DomainException("Audit BeforeJson exceeds maximum length of 4000 characters.", "AUDIT_PAYLOAD_TOO_LARGE");

        if (sanitizedAfter != null && sanitizedAfter.Length > 4000)
            throw new DomainException("Audit AfterJson exceeds maximum length of 4000 characters.", "AUDIT_PAYLOAD_TOO_LARGE");

        var log = new AuditLog(
            0,
            action,
            entityName,
            entityId,
            userId,
            terminalId,
            details,
            success,
            userName,
            reason,
            sanitizedBefore,
            sanitizedAfter,
            ipAddress,
            correlationId,
            failsafeInstanceId,
            clearOperationId,
            targetSettingsSha256);

        await _auditRepo.RecordAsync(log, ct);
        await _unitOfWork.SaveChangesAsync(ct);
    }

    private static AuditLogDto MapToDto(AuditLog log)
    {
        return new AuditLogDto(
            log.Id,
            log.CreatedAtUtc,
            log.UserId,
            log.UserName,
            log.TerminalId,
            log.Action,
            log.EntityName,
            log.EntityId,
            log.Details,
            log.Reason,
            log.BeforeJson,
            log.AfterJson,
            log.IpAddress,
            log.CorrelationId,
            log.FailsafeInstanceId,
            log.ClearOperationId,
            log.TargetSettingsSha256,
            log.Success);
    }

    private static string EscapeCsv(string? field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        return field;
    }
}
