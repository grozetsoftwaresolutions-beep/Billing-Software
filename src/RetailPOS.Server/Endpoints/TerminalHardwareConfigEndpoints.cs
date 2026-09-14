using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class TerminalHardwareConfigEndpoints
{
    public static void MapTerminalHardwareConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/terminals/{terminalId:guid}/hardware-config");

        // Read configuration - Terminal isolation check
        group.MapGet("/", async (
            Guid terminalId,
            HttpContext httpContext,
            ITerminalHardwareConfigService service,
            CancellationToken ct) =>
        {
            var userRole = httpContext.Items.TryGetValue("CurrentUserRole", out var rObj) && rObj is RoleType r ? r : RoleType.Cashier;
            var callingTerminalId = httpContext.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var tidObj) && tidObj is Guid tid
                ? tid
                : (Guid?)null;

            // Terminal Isolation Invariant: Non-admin/owner terminals can only access their own configuration
            if (userRole != RoleType.Admin && userRole != RoleType.Owner)
            {
                if (callingTerminalId.HasValue && callingTerminalId.Value != terminalId)
                {
                    return Results.Json(
                        ApiResponse.Fail<TerminalHardwareConfigDto>("Terminal is not authorized to read hardware configuration of another terminal.", "TERMINAL_MISMATCH"),
                        statusCode: StatusCodes.Status403Forbidden);
                }
            }

            var result = await service.GetConfigAsync(terminalId, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetTerminalHardwareConfig")
        .WithSummary("Retrieves peripheral hardware configuration for a specific POS terminal.");

        // Update configuration - Admin & Owner only
        group.MapPut("/", async (
            Guid terminalId,
            UpdateTerminalHardwareConfigRequest request,
            HttpContext httpContext,
            ITerminalHardwareConfigService service,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var result = await service.UpdateConfigAsync(terminalId, request, userId, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("UpdateTerminalHardwareConfig")
        .WithSummary("Updates peripheral hardware configuration for a specific POS terminal (Admin/Owner only).");
    }
}
