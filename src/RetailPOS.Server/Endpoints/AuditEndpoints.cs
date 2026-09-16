using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Audit;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/audit")
            .RequirePermission(Permission.ViewAuditLogs);

        group.MapGet("", async (
            string? action,
            string? entityName,
            string? entityId,
            Guid? userId,
            Guid? terminalId,
            DateTime? fromUtc,
            DateTime? toUtc,
            bool? success,
            int? page,
            int? pageSize,
            IAuditService auditService,
            CancellationToken ct) =>
        {
            var filter = new AuditLogQueryFilter
            {
                Action = action,
                EntityName = entityName,
                UserId = userId,
                TerminalId = terminalId,
                FromDateUtc = fromUtc,
                ToDateUtc = toUtc,
                Success = success,
                PageNumber = page.GetValueOrDefault(1) > 0 ? page.GetValueOrDefault(1) : 1,
                PageSize = pageSize.GetValueOrDefault(50) > 0 ? pageSize.GetValueOrDefault(50) : 50
            };

            var result = await auditService.QueryAsync(filter, ct);
            return Results.Ok(ApiResponse.Ok(result));
        })
        .WithName("QueryAuditLogs")
        .WithSummary("Queries immutable audit logs with filtering.");

        group.MapGet("/export", async (
            string? action,
            string? entityName,
            string? entityId,
            Guid? userId,
            Guid? terminalId,
            DateTime? fromUtc,
            DateTime? toUtc,
            bool? success,
            IAuditService auditService,
            CancellationToken ct) =>
        {
            var filter = new AuditLogQueryFilter
            {
                Action = action,
                EntityName = entityName,
                UserId = userId,
                TerminalId = terminalId,
                FromDateUtc = fromUtc,
                ToDateUtc = toUtc,
                Success = success
            };

            var csv = await auditService.ExportCsvAsync(filter, ct);
            var bytes = Encoding.UTF8.GetBytes(csv);
            return Results.File(bytes, "text/csv", "audit_logs.csv");
        })
        .RequirePermission(Permission.ExportAuditLogs)
        .WithName("ExportAuditLogs")
        .WithSummary("Exports audit logs as a CSV file.");

        return endpoints;
    }
}
