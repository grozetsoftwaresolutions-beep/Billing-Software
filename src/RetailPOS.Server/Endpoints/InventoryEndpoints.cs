using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/inventory");

        // Management: Get exact stock balance for product (Forbidden to cashiers)
        group.MapGet("/stock/{productId:guid}", async (
            Guid productId,
            IInventoryService inventoryService,
            CancellationToken ct) =>
        {
            var result = await inventoryService.GetStockBalanceAsync(productId, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetStockBalance")
        .WithSummary("Gets authoritative stock balance for a product.");

        // Management: Get low stock products
        group.MapGet("/low-stock", async (
            int? page,
            int? pageSize,
            IInventoryService inventoryService,
            CancellationToken ct) =>
        {
            var result = await inventoryService.GetLowStockProductsAsync(page ?? 1, pageSize ?? 20, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetLowStockProducts")
        .WithSummary("Lists products at or below reorder level.");

        // Management: Get stock movement history
        group.MapGet("/movements", async (
            Guid? productId,
            int? page,
            int? pageSize,
            IInventoryService inventoryService,
            CancellationToken ct) =>
        {
            var result = await inventoryService.GetProductStockHistoryAsync(productId, page ?? 1, pageSize ?? 20, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetStockMovements")
        .WithSummary("Lists append-only stock movement audit ledger.");

        // Management: Perform atomic stock adjustment
        group.MapPost("/adjustments", async (
            StockAdjustmentRequest request,
            HttpContext context,
            IInventoryService inventoryService,
            CancellationToken ct) =>
        {
            var username = (string)context.Items["CurrentUsername"]!;
            var userId = context.Items.TryGetValue("CurrentUserId", out var uidObj) && uidObj is Guid uid ? uid : (Guid?)null;
            var terminalId = context.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var termObj) && termObj is Guid tid ? tid : (Guid?)null;

            var result = await inventoryService.AdjustStockAsync(request, userId, terminalId, username, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("AdjustStock")
        .WithSummary("Performs an authorized atomic stock adjustment.");

        return endpoints;
    }
}
