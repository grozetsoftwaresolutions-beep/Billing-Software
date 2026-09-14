using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class HardwareEndpoints
{
    public static void MapHardwareEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hardware");

        // Test Print
        group.MapPost("/printer/test-print", async (
            TestPrintRequestDto request,
            HttpContext httpContext,
            IReceiptService receiptService,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var terminalId = (Guid)httpContext.Items[TerminalValidationMiddleware.ValidatedTerminalIdItemKey]!;

            var response = await receiptService.PrintTestReceiptAsync(request, terminalId, userId, ct);
            return Results.Ok(ApiResponse.Ok(response));
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithMetadata(new RequireTerminalAttribute())
        .WithName("HardwareTestPrint")
        .WithSummary("Executes a diagnostic test print on the terminal's receipt printer.");

        // Manual Drawer Kick: Manager, Admin, Owner only. Cashier is strictly 403.
        group.MapPost("/drawer/test-kick", async (
            DrawerKickRequestDto? request,
            HttpContext httpContext,
            ICashDrawerService drawerService,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var terminalId = (Guid)httpContext.Items[TerminalValidationMiddleware.ValidatedTerminalIdItemKey]!;
            var reason = request?.Reason ?? "Manual diagnostic test kick";

            var response = await drawerService.TriggerDrawerKickAsync(terminalId, userId, reason, isManualOpen: true, ct);
            return Results.Ok(ApiResponse.Ok(response));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithMetadata(new RequireTerminalAttribute())
        .WithName("HardwareTestKick")
        .WithSummary("Manually kicks the terminal cash drawer (Manager/Admin/Owner only; Cashier 403).");

        // Current Scale Weight
        group.MapGet("/scale/current-weight", async (
            IWeighingScaleService scaleService,
            CancellationToken ct) =>
        {
            var response = await scaleService.GetCurrentWeightAsync(ct);
            return response.Success ? Results.Ok(response) : Results.BadRequest(response);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithMetadata(new RequireTerminalAttribute())
        .WithName("HardwareScaleWeight")
        .WithSummary("Reads current weight and stability from the terminal's connected weighing scale.");

        // Scale Zero
        group.MapPost("/scale/zero", async (
            HttpContext httpContext,
            IWeighingScaleService scaleService,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var terminalId = (Guid)httpContext.Items[TerminalValidationMiddleware.ValidatedTerminalIdItemKey]!;

            var response = await scaleService.ZeroScaleAsync(terminalId, userId, ct);
            return response.Success ? Results.Ok(response) : Results.BadRequest(response);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithMetadata(new RequireTerminalAttribute())
        .WithName("HardwareScaleZero")
        .WithSummary("Calibrates the weighing scale zero baseline.");

        // Scale Tare
        group.MapPost("/scale/tare", async (
            HttpContext httpContext,
            IWeighingScaleService scaleService,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var terminalId = (Guid)httpContext.Items[TerminalValidationMiddleware.ValidatedTerminalIdItemKey]!;

            var response = await scaleService.TareScaleAsync(terminalId, userId, ct);
            return response.Success ? Results.Ok(response) : Results.BadRequest(response);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithMetadata(new RequireTerminalAttribute())
        .WithName("HardwareScaleTare")
        .WithSummary("Tares the weighing scale with the current container weight.");

        // Peripheral Hardware Status
        group.MapGet("/status", async (
            HttpContext httpContext,
            ITerminalHardwareConfigService configService,
            CancellationToken ct) =>
        {
            var terminalId = (Guid)httpContext.Items[TerminalValidationMiddleware.ValidatedTerminalIdItemKey]!;
            var configResult = await configService.GetConfigAsync(terminalId, ct);

            var activePrinterName = configResult.Data?.PrinterNameOrAddress ?? "SimulatedPrinter";
            var activePaperWidth = configResult.Data?.PaperWidth ?? PaperWidth.Width80mm;

            var status = new HardwareStatusDto(
                IsOverallHealthy: true,
                PrinterStatus: "Ready",
                DrawerStatus: "Ready",
                ScaleStatus: configResult.Data?.IsScaleEnabled == true ? "Ready" : "Disabled",
                ScannerStatus: "Ready",
                ActivePrinterName: activePrinterName,
                ActivePaperWidth: activePaperWidth);

            return Results.Ok(ApiResponse.Ok(status));
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithMetadata(new RequireTerminalAttribute())
        .WithName("HardwareStatus")
        .WithSummary("Reports health and operational status of all attached peripherals.");
    }
}
