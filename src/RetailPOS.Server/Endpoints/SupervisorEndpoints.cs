using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Security;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Security;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class SupervisorEndpoints
{
    public static IEndpointRouteBuilder MapSupervisorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth");

        group.MapPost("/supervisor-approve", async (
            SupervisorApprovalRequest request,
            HttpContext httpContext,
            ISupervisorApprovalService supervisorService,
            CancellationToken ct) =>
        {
            var requestingUserId = (Guid)httpContext.Items["CurrentUserId"]!;
            var sessionHash = (string)httpContext.Items["CurrentSessionIdentityHash"]!;
            var terminalId = (Guid)(httpContext.Items["CurrentSessionTerminalId"] ?? Guid.Empty);

            var response = await supervisorService.IssueApprovalTokenAsync(
                request, requestingUserId, sessionHash, terminalId, ct);
            return Results.Ok(ApiResponse.Ok(response));
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("IssueSupervisorApprovalToken")
        .WithSummary("Issues a single-use operation-bound supervisor approval token.");

        group.MapPost("/lock", (
            HttpContext httpContext,
            ISessionManager sessionManager) =>
        {
            if (httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                var token = authHeader.ToString()["Bearer ".Length..].Trim();
                sessionManager.LockSession(token);
            }
            return Results.Ok(ApiResponse.Ok("Session locked."));
        })
        .WithName("LockTerminalSession")
        .WithSummary("Locks the terminal session immediately.");

        group.MapPost("/unlock", async (
            TerminalUnlockRequest request,
            HttpContext httpContext,
            ISessionManager sessionManager,
            CancellationToken ct) =>
        {
            if (!httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                return Results.Json(ApiResponse.Fail<TerminalUnlockResponse>("Authorization required.", "UNAUTHENTICATED"), statusCode: StatusCodes.Status401Unauthorized);
            }

            var token = authHeader.ToString()["Bearer ".Length..].Trim();
            var success = await sessionManager.UnlockSessionAsync(token, request.Pin, ct);
            if (!success)
            {
                return Results.Json(ApiResponse.Fail<TerminalUnlockResponse>("Invalid PIN or account locked.", "INVALID_PIN"), statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Ok(ApiResponse.Ok(new TerminalUnlockResponse(true, "Session successfully unlocked.", false, (string)httpContext.Items["CurrentUsername"]!, null)));
        })
        .WithName("UnlockTerminalSession")
        .WithSummary("Unlocks a locked terminal session with the user's PIN.");

        return endpoints;
    }
}
