using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Common;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

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
            var remoteIp = httpContext.Connection.RemoteIpAddress;
            if (remoteIp != null && !LoopbackRecoveryMiddleware.IsLoopbackAddress(remoteIp))
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

        group.MapPost("/provision-replacement-identity", async (
            ProvisionReplacementIdentityRequestDto request,
            IServerIdentityService serverIdentityService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var remoteIp = httpContext.Connection.RemoteIpAddress;
            if (remoteIp != null && !LoopbackRecoveryMiddleware.IsLoopbackAddress(remoteIp))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.StoreMasterRecoveryKey) || request.StoreMasterRecoveryKey.Length != 64)
            {
                return Results.BadRequest(ApiResponse.Fail<object>("StoreMasterRecoveryKey must be a 64-character hexadecimal key.", "INVALID_MASTER_KEY"));
            }

            try
            {
                var response = await serverIdentityService.ProvisionReplacementIdentityAsync(request, ct);
                return Results.Ok(ApiResponse.Ok(response, "Replacement server identity provisioned successfully."));
            }
            catch (DomainException ex) when (ex.ErrorCode == "INVALID_MASTER_KEY")
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (DomainException ex) when (ex.ErrorCode == "IDENTITY_ALREADY_EXISTS" || ex.ErrorCode == "SERVER_IDENTITY_ALREADY_VALID")
            {
                return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        });

        return app;
    }
}
