using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class SaleEndpoints
{
    public static IEndpointRouteBuilder MapSaleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/sales");

        // Live cart calculation with store rounding
        group.MapPost("/calculate", async (
            CalculateSaleRequest request,
            ISalesService salesService,
            CancellationToken ct) =>
        {
            var result = await salesService.CalculateCartAsync(request, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("CalculateCart")
        .WithSummary("Calculates item totals, taxes, and estimated rounding for cart.");

        // Authoritative checkout
        group.MapPost("", async (
            CreateSaleRequest request,
            HttpContext httpContext,
            ISalesService salesService,
            CancellationToken ct) =>
        {
            var cashierId = (Guid)httpContext.Items["CurrentUserId"]!;
            var cashierName = (string)httpContext.Items["CurrentUsername"]!;

            var terminalId = httpContext.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var tidObj) && tidObj is Guid tid
                ? tid
                : (httpContext.Items.TryGetValue("CurrentSessionTerminalId", out var sidObj) && sidObj is Guid sid ? sid : Guid.Empty);

            var result = await salesService.CreateSaleAsync(request, cashierId, cashierName, terminalId, ct);
            return result.Success
                ? Results.Created($"/api/v1/sales/{result.Data!.Id}", result)
                : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithMetadata(new RequireTerminalAttribute())
        .WithName("CreateSale")
        .WithSummary("Executes authoritative checkout with inventory deduction and payments.");

        // Lookup sale by ID
        group.MapGet("/{id:guid}", async (
            Guid id,
            ISalesService salesService,
            CancellationToken ct) =>
        {
            var result = await salesService.GetSaleByIdAsync(id, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetSaleById")
        .WithSummary("Gets sale invoice details by ID.");

        // Lookup sale by Invoice Number
        group.MapGet("/invoice/{invoiceNumber}", async (
            string invoiceNumber,
            ISalesService salesService,
            CancellationToken ct) =>
        {
            var result = await salesService.GetSaleByInvoiceNumberAsync(invoiceNumber, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetSaleByInvoiceNumber")
        .WithSummary("Gets sale invoice details by invoice number.");

        // Search sales
        group.MapGet("", async (
            DateTime? fromUtc,
            DateTime? toUtc,
            Guid? cashierId,
            Guid? terminalId,
            string? invoiceNumber,
            int? page,
            int? pageSize,
            ISalesService salesService,
            CancellationToken ct) =>
        {
            var request = new SaleSearchRequest(
                fromUtc,
                toUtc,
                cashierId,
                terminalId,
                invoiceNumber,
                page ?? 1,
                pageSize ?? 20);

            var result = await salesService.SearchSalesAsync(request, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("SearchSales")
        .WithSummary("Searches sales with filtering and pagination.");

        // Elevated refund
        group.MapPost("/{id:guid}/refund", async (
            Guid id,
            RefundSaleRequest request,
            HttpContext httpContext,
            ISalesService salesService,
            CancellationToken ct) =>
        {
            var authorizedByUserId = (Guid)httpContext.Items["CurrentUserId"]!;
            var authorizedByUserName = (string)httpContext.Items["CurrentUsername"]!;

            var terminalId = httpContext.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var tidObj) && tidObj is Guid tid
                ? tid
                : (httpContext.Items.TryGetValue("CurrentSessionTerminalId", out var sidObj) && sidObj is Guid sid ? sid : Guid.Empty);

            var result = await salesService.RefundSaleAsync(id, request, authorizedByUserId, authorizedByUserName, terminalId, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithMetadata(new RequireTerminalAttribute())
        .WithName("RefundSale")
        .WithSummary("Processes item returns, restores stock, and registers refund.");

        // Get refunds for a sale
        group.MapGet("/{id:guid}/refunds", async (
            Guid id,
            ISalesService salesService,
            CancellationToken ct) =>
        {
            var result = await salesService.GetRefundsForSaleAsync(id, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetSaleRefunds")
        .WithSummary("Gets all refunds issued against a sale invoice.");

        return endpoints;
    }
}
