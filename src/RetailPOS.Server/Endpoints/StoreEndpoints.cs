using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Configuration;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class StoreEndpoints
{
    public static IEndpointRouteBuilder MapStoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/store");

        // Read store settings (including CashRoundingEnabled & TimeZoneId)
        group.MapGet("/settings", async (
            IStoreService storeService,
            CancellationToken ct) =>
        {
            var result = await storeService.GetSettingsAsync(ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetStoreSettings")
        .WithSummary("Gets current store settings and configuration.");

        // Update store settings (Admin / Owner only)
        group.MapPut("/settings", async (
            UpdateStoreSettingsRequest request,
            HttpContext context,
            IStoreService storeService,
            CancellationToken ct) =>
        {
            var username = (string)context.Items["CurrentUsername"]!;
            var result = await storeService.UpdateSettingsAsync(request, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("UpdateStoreSettings")
        .WithSummary("Updates store billing, rounding, and timezone settings.");

        return endpoints;
    }
}
