namespace RetailPOS.Server.Endpoints;

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Common;

public static class AdminRecoveryEndpoints
{
    public static IEndpointRouteBuilder MapAdminRecoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/recovery");

        group.MapPost("/restore", async (
            EmergencyRestoreRequestDto request,
            IBackupService backupService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // Security requirement: enforce localhost/loopback connection only
            var remoteIp = httpContext.Connection.RemoteIpAddress;
            if (remoteIp != null && !IPAddress.IsLoopback(remoteIp))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.StoreMasterRecoveryKey) || request.StoreMasterRecoveryKey.Length != 64)
            {
                return Results.BadRequest(ApiResponse.Fail<object>("StoreMasterRecoveryKey must be a 64-character hexadecimal key.", "INVALID_MASTER_KEY"));
            }

            var keyChars = request.StoreMasterRecoveryKey.ToCharArray();
            try
            {
                await backupService.EmergencyRestoreAsync(request.FileName, keyChars, ct).ConfigureAwait(false);
                return Results.Ok(ApiResponse.Ok(new { status = "Restored" }, "Emergency restore completed successfully."));
            }
            catch (DomainException ex) when (ex.ErrorCode == "INVALID_MASTER_KEY")
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        });

        return app;
    }
}
