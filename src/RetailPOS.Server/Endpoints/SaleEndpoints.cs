using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;
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

        // --- PHASE 10 HARDWARE PRINT & REPRINT LIFECYCLE ---

        // Direct Server-Side Print
        group.MapPost("/{id:guid}/print", async (
            Guid id,
            HttpContext httpContext,
            IReceiptService receiptService,
            CancellationToken ct) =>
        {
            var cashierId = (Guid)httpContext.Items["CurrentUserId"]!;
            var terminalId = httpContext.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var tidObj) && tidObj is Guid tid
                ? tid
                : (httpContext.Items.TryGetValue("CurrentSessionTerminalId", out var sidObj) && sidObj is Guid sid ? sid : Guid.Empty);

            var result = await receiptService.PrintDirectSaleReceiptAsync(id, terminalId, cashierId, isReprint: false, ct);
            return Results.Ok(ApiResponse.Ok(result));
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("PrintSaleReceipt")
        .WithSummary("Generates and prints receipt for a sale invoice.");

        // Direct Server-Side Reprint
        group.MapPost("/{id:guid}/reprint", async (
            Guid id,
            HttpContext httpContext,
            IReceiptService receiptService,
            CancellationToken ct) =>
        {
            var cashierId = (Guid)httpContext.Items["CurrentUserId"]!;
            var terminalId = httpContext.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var tidObj) && tidObj is Guid tid
                ? tid
                : (httpContext.Items.TryGetValue("CurrentSessionTerminalId", out var sidObj) && sidObj is Guid sid ? sid : Guid.Empty);

            var result = await receiptService.PrintDirectSaleReceiptAsync(id, terminalId, cashierId, isReprint: true, ct);
            return Results.Ok(ApiResponse.Ok(result));
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("ReprintSaleReceipt")
        .WithSummary("Generates and prints a marked reprint receipt, incrementing reprint counter upon success.");

        // Two-Phase Reprint: Step 1 - Generate Payload
        group.MapPost("/{id:guid}/reprint-payload", async (
            Guid id,
            HttpContext httpContext,
            IReceiptService receiptService,
            ISalesService salesService,
            CancellationToken ct) =>
        {
            var terminalId = httpContext.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var tidObj) && tidObj is Guid tid
                ? tid
                : (httpContext.Items.TryGetValue("CurrentSessionTerminalId", out var sidObj) && sidObj is Guid sid ? sid : Guid.Empty);

            var invoiceResult = await salesService.GetSaleByIdAsync(id, ct);
            if (!invoiceResult.Success || invoiceResult.Data == null)
            {
                return Results.NotFound(ApiResponse.Fail<RenderedReceiptPayloadDto>("Sale invoice not found.", "SALE_NOT_FOUND"));
            }

            var payload = await receiptService.GenerateSaleReceiptPayloadAsync(id, terminalId, isReprint: true, ct);
            var dto = new RenderedReceiptPayloadDto(
                id,
                invoiceResult.Data.InvoiceNumber,
                payload.RawBytes,
                payload.AsciiPreview,
                payload.PendingReprintCount);

            return Results.Ok(ApiResponse.Ok(dto));
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GenerateReprintPayload")
        .WithSummary("Renders ESC/POS reprint payload with watermark without incrementing reprint count.");

        // Two-Phase Reprint: Step 2A - Confirm Print Success
        group.MapPost("/{id:guid}/confirm-reprint", async (
            Guid id,
            ConfirmReprintRequest? request,
            HttpContext httpContext,
            IReceiptService receiptService,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var terminalId = httpContext.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var tidObj) && tidObj is Guid tid
                ? tid
                : (httpContext.Items.TryGetValue("CurrentSessionTerminalId", out var sidObj) && sidObj is Guid sid ? sid : Guid.Empty);

            var result = await receiptService.ConfirmReprintAsync(id, terminalId, userId, request?.Reason, ct);
            return Results.Ok(ApiResponse.Ok(result));
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("ConfirmReprint")
        .WithSummary("Confirms physical print completion, atomically increments ReprintCount, and writes audit record.");

        // Two-Phase Reprint: Step 2B - Report Print Failed
        group.MapPost("/{id:guid}/report-reprint-failed", async (
            Guid id,
            ReportReprintFailedRequest request,
            HttpContext httpContext,
            IReceiptService receiptService,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var terminalId = httpContext.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var tidObj) && tidObj is Guid tid
                ? tid
                : (httpContext.Items.TryGetValue("CurrentSessionTerminalId", out var sidObj) && sidObj is Guid sid ? sid : Guid.Empty);

            var result = await receiptService.ReportReprintFailedAsync(id, terminalId, userId, request.ErrorMessage, ct);
            return Results.Ok(ApiResponse.Ok(result));
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("ReportReprintFailed")
        .WithSummary("Reports print I/O failure, logs audit event, and leaves ReprintCount unchanged.");

        
        // Phase 13: Void sale with payment & inventory reconciliation
        group.MapPost("/{id:guid}/void", async (
            Guid id,
            VoidSaleRequest request,
            HttpContext httpContext,
            ISalesService salesService,
            CancellationToken ct) =>
        {
            var cashierId = (Guid)httpContext.Items["CurrentUserId"]!;
            var cashierName = (string)httpContext.Items["CurrentUsername"]!;
            var sessionHash = (string?)(httpContext.Items["CurrentSessionIdentityHash"]) ?? string.Empty;
            var terminalId = (Guid)(httpContext.Items["CurrentSessionTerminalId"] ?? Guid.Empty);

            var result = await salesService.VoidSaleAsync(id, request, cashierId, cashierName, sessionHash, terminalId, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("VoidSale")
        .WithSummary("Voids a completed sale invoice with payment and inventory reconciliation.");

        return endpoints;
    }
}
