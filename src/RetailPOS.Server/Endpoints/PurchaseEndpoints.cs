using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Purchases;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class PurchaseEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/purchases");

        // Create Purchase Invoice (Manager, Admin, Owner)
        group.MapPost("", async (
            CreatePurchaseRequest request,
            HttpContext httpContext,
            IPurchaseService purchaseService,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;

            var terminalId = httpContext.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var tidObj) && tidObj is Guid tid
                ? tid
                : (httpContext.Items.TryGetValue("CurrentSessionTerminalId", out var sidObj) && sidObj is Guid sid ? sid : Guid.Empty);

            var result = await purchaseService.CreatePurchaseAsync(request, userId, username, terminalId, ct);
            return result.Success
                ? Results.Created($"/api/v1/purchases/{result.Data!.Id}", result)
                : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("CreatePurchase")
        .WithSummary("Inwards stock and creates a purchase invoice with server-authoritative tax calculation.");

        // Get Purchase Invoice by ID (Manager, Admin, Owner)
        group.MapGet("/{id:guid}", async (
            Guid id,
            IPurchaseService purchaseService,
            CancellationToken ct) =>
        {
            var result = await purchaseService.GetByIdAsync(id, ct);
            return result.Success
                ? Results.Ok(result)
                : Results.NotFound(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetPurchaseById")
        .WithSummary("Retrieves a purchase invoice by ID.");

        // Search Purchase Invoices (Manager, Admin, Owner)
        group.MapGet("", async (
            Guid? supplierId,
            DateTime? fromDateUtc,
            DateTime? toDateUtc,
            int? paymentStatus,
            int? status,
            string? searchTerm,
            int? page,
            int? pageSize,
            IPurchaseService purchaseService,
            CancellationToken ct) =>
        {
            var result = await purchaseService.SearchAsync(
                supplierId,
                fromDateUtc,
                toDateUtc,
                paymentStatus,
                status,
                searchTerm,
                page ?? 1,
                pageSize ?? 20,
                ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("SearchPurchases")
        .WithSummary("Searches purchase invoices with filters and pagination.");

        // Cancel Purchase Invoice (Admin, Owner only! Manager CANNOT cancel)
        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            CancelPurchaseRequest request,
            HttpContext httpContext,
            IPurchaseService purchaseService,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;

            var result = await purchaseService.CancelPurchaseAsync(id, request, userId, username, ct);
            return result.Success
                ? Results.Ok(result)
                : (result.ErrorCode == "PURCHASE_NOT_FOUND" ? Results.NotFound(result) : Results.BadRequest(result));
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("CancelPurchase")
        .WithSummary("Reverses stock and cancels a purchase invoice according to settlement mode (Admin/Owner only).");

        return endpoints;
    }
}
