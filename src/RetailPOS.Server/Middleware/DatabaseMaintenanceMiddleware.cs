namespace RetailPOS.Server.Middleware;

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RetailPOS.Application.Services;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;

public sealed class DatabaseMaintenanceMiddleware
{
    private readonly RequestDelegate _next;

    public DatabaseMaintenanceMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IDatabaseOperationGate gate)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Bypass maintenance gate for emergency recovery, health, backup status queries, or rotation orchestration
        if (path.StartsWith("/api/v1/admin/recovery", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/backups/settings", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/backups/history", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/admin/security/confirm-recovery-key-rotation", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/admin/security/cancel-recovery-key-rotation", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }


        IOperationLease lease;
        try
        {
            lease = await gate.AcquireExecutionLeaseAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (DomainException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";
            var payload = new
            {
                error = "SYSTEM_MAINTENANCE",
                message = "The server database is currently undergoing maintenance or backup/restore operations. Please retry shortly."
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
            return;
        }

        using (lease)
        {
            await _next(context).ConfigureAwait(false);
        }
    }
}
