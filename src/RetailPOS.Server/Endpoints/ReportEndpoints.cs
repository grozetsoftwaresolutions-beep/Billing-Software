using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Reports;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/reports");

        // Helper to resolve session terminal ID
        static Guid? GetSessionTerminalId(HttpContext httpContext)
        {
            if (httpContext.Items.TryGetValue("CurrentSessionTerminalId", out var tIdObj) && tIdObj is Guid tId)
                return tId;
            if (httpContext.Items.TryGetValue("CurrentTerminalId", out var tIdObj2) && tIdObj2 is Guid tId2)
                return tId2;
            return null;
        }

        // 1. Daily Sales Summary (Cashier, Manager, Admin, Owner)
        group.MapGet("/daily-sales", async (
            DateTime? fromDate,
            DateTime? toDate,
            Guid? terminalId,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var sessionTerminalId = GetSessionTerminalId(httpContext);
            var query = new ReportDateRangeQuery(fromDate, toDate, terminalId ?? sessionTerminalId);
            var result = await service.GetDailySalesSummaryAsync(query, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetDailySalesSummaryReport");

        // 2. Product Performance (Manager, Admin, Owner)
        group.MapGet("/product-sales", async (
            DateTime? fromDate,
            DateTime? toDate,
            int? categoryId,
            Guid? productId,
            int? page,
            int? pageSize,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var query = new ReportDateRangeQuery(fromDate, toDate, CategoryId: categoryId, Page: page ?? 1, PageSize: pageSize ?? 50);
            var result = await service.GetProductPerformanceAsync(query, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        // 3. Stock Valuation (Cashier with cost masking; Manager, Admin, Owner full)
        group.MapGet("/stock-valuation", async (
            int? categoryId,
            bool? lowStockOnly,
            string? searchTerm,
            int? page,
            int? pageSize,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var role = (RoleType)httpContext.Items["CurrentUserRole"]!;
            var query = new ReportDateRangeQuery(CategoryId: categoryId, Page: page ?? 1, PageSize: pageSize ?? 50);

            var result = await service.GetStockValuationAsync(
                query, lowStockOnly ?? false, searchTerm, role, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        // 4. Stock Movement Ledger (Manager, Admin, Owner)
        group.MapGet("/stock-ledger/{productId:guid}", async (
            Guid productId,
            DateTime? fromDate,
            DateTime? toDate,
            int? page,
            int? pageSize,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var query = new ReportDateRangeQuery(fromDate, toDate, Page: page ?? 1, PageSize: pageSize ?? 50);
            var result = await service.GetStockMovementLedgerAsync(productId, query, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        // 5. Purchase Summary (Manager, Admin, Owner)
        group.MapGet("/purchases", async (
            DateTime? fromDate,
            DateTime? toDate,
            Guid? supplierId,
            int? page,
            int? pageSize,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var query = new ReportDateRangeQuery(fromDate, toDate, SupplierId: supplierId, Page: page ?? 1, PageSize: pageSize ?? 50);
            var result = await service.GetPurchaseSummaryAsync(query, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        // 6. Supplier Balances (Manager, Admin, Owner)
        group.MapGet("/supplier-balances", async (
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await service.GetSupplierBalancesAsync(username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        // 7. Cashier Performance / Cashier Summary
        group.MapGet("/cashier-summary", async (
            DateTime? fromDate,
            DateTime? toDate,
            Guid? cashierUserId,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var role = (RoleType)httpContext.Items["CurrentUserRole"]!;

            if (role == RoleType.Cashier && cashierUserId.HasValue && cashierUserId.Value != userId)
            {
                return Results.Json(ApiResponse.Fail<CashierPerformanceReportDto>(
                    "Cashiers are only authorized to view their own cashier summary.", "FORBIDDEN"), statusCode: StatusCodes.Status403Forbidden);
            }

            var query = new ReportDateRangeQuery(fromDate, toDate, CashierUserId: cashierUserId);
            var result = await service.GetCashierPerformanceAsync(query, role, userId, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        // 8. Current Terminal Operational Snapshot
        group.MapGet("/current-terminal", async (
            Guid? terminalId,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var role = (RoleType)httpContext.Items["CurrentUserRole"]!;
            var sessionTerminalId = GetSessionTerminalId(httpContext);

            if (role == RoleType.Cashier && terminalId.HasValue && sessionTerminalId.HasValue && terminalId.Value != sessionTerminalId.Value)
            {
                return Results.Json(ApiResponse.Fail<CurrentTerminalReportDto>(
                    "Cashiers are only authorized to view their assigned terminal.", "FORBIDDEN"), statusCode: StatusCodes.Status403Forbidden);
            }

            var targetTerminalId = terminalId ?? sessionTerminalId ?? Guid.Empty;
            if (targetTerminalId == Guid.Empty)
            {
                return Results.BadRequest(ApiResponse.Fail<CurrentTerminalReportDto>("TerminalId is required.", "INVALID_TERMINAL_ID"));
            }

            var result = await service.GetCurrentTerminalReportAsync(targetTerminalId, role, sessionTerminalId, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        // 9. Payment Tender Breakdown
        group.MapGet("/payment-summary", async (
            DateTime? fromDate,
            DateTime? toDate,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var query = new ReportDateRangeQuery(fromDate, toDate);
            var result = await service.GetPaymentSummaryAsync(query, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        // 10. Profit & Margin Estimate
        group.MapGet("/profit-estimate", async (
            DateTime? fromDate,
            DateTime? toDate,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var query = new ReportDateRangeQuery(fromDate, toDate);
            var result = await service.GetProfitEstimateAsync(query, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        // 11. GSTR-1 Aligned Tax Summary
        group.MapGet("/tax-summary", async (
            DateTime? fromDate,
            DateTime? toDate,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var query = new ReportDateRangeQuery(fromDate, toDate);
            var result = await service.GetTaxSummaryAsync(query, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        // 12. Customer Credit Balances (aliases: /customer-credit and /customer-balances)
        static async Task<IResult> GetCustomerBalancesHandler(
            int? page,
            int? pageSize,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct)
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await service.GetCustomerCreditSummaryAsync(page ?? 1, pageSize ?? 50, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }

        group.MapGet("/customer-credit", GetCustomerBalancesHandler)
            .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapGet("/customer-balances", GetCustomerBalancesHandler)
            .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        // 13. Audit Trail Activity
        group.MapGet("/audit-activity", async (
            DateTime? fromDate,
            DateTime? toDate,
            string? moduleName,
            string? action,
            string? performedBy,
            int? page,
            int? pageSize,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var query = new ReportDateRangeQuery(fromDate, toDate, Page: page ?? 1, PageSize: pageSize ?? 50);
            var result = await service.GetAuditActivityAsync(query, moduleName, action, performedBy, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Admin, RoleType.Owner);

        // 14. Streaming CSV Export
        group.MapGet("/{reportType}/csv", async (
            string reportType,
            DateTime? fromDate,
            DateTime? toDate,
            Guid? terminalId,
            Guid? cashierUserId,
            int? categoryId,
            Guid? supplierId,
            Guid? customerId,
            IReportService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var role = (RoleType)httpContext.Items["CurrentUserRole"]!;
            var sessionTerminalId = GetSessionTerminalId(httpContext);

            var normalizedType = reportType.Trim().ToLowerInvariant();
            if (normalizedType == "customer-credit")
                normalizedType = "customer-balances";

            // Enforce role authorization mirroring JSON endpoints
            if (role == RoleType.Cashier)
            {
                var restrictedForCashier = new HashSet<string>
                {
                    "product-sales", "stock-movement", "purchases", "supplier-balances",
                    "profit-estimate", "tax-summary", "audit-activity", "customer-balances", "customer-credit"
                };

                if (restrictedForCashier.Contains(normalizedType))
                {
                    return Results.Json(ApiResponse.Fail<bool>("Access denied for Cashier role on this report.", "FORBIDDEN"), statusCode: StatusCodes.Status403Forbidden);
                }

                if (normalizedType == "cashier-summary" && cashierUserId.HasValue && cashierUserId.Value != userId)
                {
                    return Results.Json(ApiResponse.Fail<bool>("Cashiers are only authorized to view their own summary.", "FORBIDDEN"), statusCode: StatusCodes.Status403Forbidden);
                }
            }

            var query = new ReportDateRangeQuery(
                fromDate,
                toDate,
                terminalId ?? sessionTerminalId,
                cashierUserId,
                categoryId,
                supplierId,
                customerId,
                Page: 1,
                PageSize: 200);

            return Results.Stream(
                async stream =>
                {
                    await service.WriteCsvAsync(normalizedType, query, stream, role, userId, username, sessionTerminalId, ct);
                },
                contentType: "text/csv; charset=utf-8",
                fileDownloadName: $"{normalizedType}_{DateTime.UtcNow:yyyyMMdd}.csv");
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        return endpoints;
    }
}
