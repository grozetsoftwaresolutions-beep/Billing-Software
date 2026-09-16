using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Security;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Security;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Security;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class SecuritySettingsEndpoints
{
    public static IEndpointRouteBuilder MapSecuritySettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/settings/security");

        group.MapGet("", async (
            ISecuritySettingsStore settingsStore,
            CancellationToken ct) =>
        {
            var s = await settingsStore.GetSettingsAsync(ct);
            var dto = new SecuritySettingsDto(
                s.IdleTimeoutMinutes,
                s.MaxFailedPinAttempts,
                s.PinLockoutMinutes,
                s.MaxCashierDiscountPercentage,
                s.RequireSupervisorForRefund,
                s.RequireSupervisorForVoid,
                s.RequireSupervisorForPriceOverride);

            var failsafe = new FailsafeStatusResponse(
                settingsStore.IsInFailsafeMode,
                settingsStore.CurrentFailsafeMarker?.FailsafeInstanceId,
                settingsStore.CurrentFailsafeMarker?.EntryTimestampUtc,
                settingsStore.CurrentFailsafeMarker?.EntryReason,
                settingsStore.CurrentFailsafeMarker?.PendingClear != null,
                settingsStore.CurrentFailsafeMarker?.PendingClear?.ClearOperationId);

            return Results.Ok(ApiResponse.Ok(new { Settings = dto, Failsafe = failsafe }));
        })
        .RequirePermission(Permission.ManageStoreSettings)
        .WithName("GetSecuritySettings")
        .WithSummary("Retrieves security settings and failsafe status.");

        group.MapPut("", async (
            UpdateSecuritySettingsRequest request,
            HttpContext httpContext,
            ISecuritySettingsStore settingsStore,
            CancellationToken ct) =>
        {
            var ownerId = (Guid)httpContext.Items["CurrentUserId"]!;
            var ownerName = (string)httpContext.Items["CurrentUsername"]!;

            var domainSettings = new SecuritySettings(
                request.IdleTimeoutMinutes,
                request.MaxFailedPinAttempts,
                request.PinLockoutMinutes,
                request.MaxCashierDiscountPercentage,
                request.RequireSupervisorForRefund,
                request.RequireSupervisorForVoid,
                request.RequireSupervisorForPriceOverride);

            await settingsStore.UpdateSettingsAsync(domainSettings, ownerId, ownerName, ct);
            return Results.Ok(ApiResponse.Ok("Security settings updated successfully."));
        })
        .RequireRole(RoleType.Owner, RoleType.Admin)
        .WithName("UpdateSecuritySettings")
        .WithSummary("Updates security settings (Owner/Admin only).");

        group.MapPost("/clear-failsafe", async (
            ClearFailsafeRequest request,
            HttpContext httpContext,
            ISecuritySettingsStore settingsStore,
            CancellationToken ct) =>
        {
            var ownerId = (Guid)httpContext.Items["CurrentUserId"]!;
            var ownerName = (string)httpContext.Items["CurrentUsername"]!;

            var domainSettings = new SecuritySettings(
                request.NewSettings.IdleTimeoutMinutes,
                request.NewSettings.MaxFailedPinAttempts,
                request.NewSettings.PinLockoutMinutes,
                request.NewSettings.MaxCashierDiscountPercentage,
                request.NewSettings.RequireSupervisorForRefund,
                request.NewSettings.RequireSupervisorForVoid,
                request.NewSettings.RequireSupervisorForPriceOverride);

            await settingsStore.ClearFailsafeAsync(domainSettings, ownerId, ownerName, ct: ct);
            return Results.Ok(ApiResponse.Ok("Failsafe cleared and normal mode restored."));
        })
        .RequireRole(RoleType.Owner)
        .WithName("ClearFailsafe")
        .WithSummary("Clears failsafe mode and restores normal operation (Owner only).");

        return endpoints;
    }
}
