using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class CashSessionEndpoints
{
    public static void MapCashSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/cash-sessions");

        // Open session supports both POST / and POST /open
        async Task<IResult> OpenHandler(
            OpenCashSessionRequest request,
            ICashSessionService service,
            HttpContext httpContext,
            CancellationToken ct)
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await service.OpenSessionAsync(request, userId, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }

        group.MapPost("/", OpenHandler)
            .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapPost("/open", OpenHandler)
            .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapGet("/current", async (
            Guid terminalId,
            ICashSessionService service,
            CancellationToken ct) =>
        {
            var result = await service.GetActiveSessionAsync(terminalId, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapGet("/{id:guid}", async (
            Guid id,
            ICashSessionService service,
            CancellationToken ct) =>
        {
            var result = await service.GetByIdAsync(id, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapPost("/{id:guid}/transactions", async (
            Guid id,
            AddCashTransactionRequest request,
            ICashSessionService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var role = (RoleType)httpContext.Items["CurrentUserRole"]!;

            var isManagerOrAbove = role is RoleType.Manager or RoleType.Admin or RoleType.Owner;
            if ((request.Type == CashTransactionType.PayOut || request.Type == CashTransactionType.CashDrop) && !isManagerOrAbove)
            {
                return Results.Json(ApiResponse.Fail<object>("PayOut and CashDrop require Manager, Admin, or Owner authorization.", "INSUFFICIENT_PERMISSIONS"), statusCode: StatusCodes.Status403Forbidden);
            }

            var authorizedByUserId = isManagerOrAbove ? userId : (Guid?)null;

            var result = await service.AddTransactionAsync(id, request, userId, username, authorizedByUserId, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapPost("/{id:guid}/close", async (
            Guid id,
            CloseCashSessionRequest request,
            ICashSessionService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await service.CloseSessionAsync(id, request, userId, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapPost("/{id:guid}/force-close", async (
            Guid id,
            ForceCloseCashSessionRequest request,
            ICashSessionService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await service.ForceCloseSessionAsync(id, request, userId, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapGet("/{id:guid}/x-report", async (
            Guid id,
            ICashSessionService service,
            CancellationToken ct) =>
        {
            var result = await service.GetXReportAsync(id, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapGet("/{id:guid}/z-report", async (
            Guid id,
            ICashSessionService service,
            CancellationToken ct) =>
        {
            var result = await service.GetZReportAsync(id, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);
    }
}
