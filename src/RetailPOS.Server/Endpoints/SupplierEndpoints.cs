using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Purchases;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class SupplierEndpoints
{
    public static IEndpointRouteBuilder MapSupplierEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/suppliers");

        // Create Supplier (Manager, Admin, Owner)
        group.MapPost("", async (
            CreateSupplierRequest request,
            HttpContext httpContext,
            ISupplierService supplierService,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await supplierService.CreateSupplierAsync(request, username, ct);
            return result.Success
                ? Results.Created($"/api/v1/suppliers/{result.Data!.Id}", result)
                : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("CreateSupplier")
        .WithSummary("Creates a new supplier profile.");

        // Update Supplier (Manager, Admin, Owner)
        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateSupplierRequest request,
            HttpContext httpContext,
            ISupplierService supplierService,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await supplierService.UpdateSupplierAsync(id, request, username, ct);
            return result.Success
                ? Results.Ok(result)
                : (result.ErrorCode == "SUPPLIER_NOT_FOUND" ? Results.NotFound(result) : Results.BadRequest(result));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("UpdateSupplier")
        .WithSummary("Updates an existing supplier profile.");

        // Get Supplier by ID (Manager, Admin, Owner)
        group.MapGet("/{id:guid}", async (
            Guid id,
            ISupplierService supplierService,
            CancellationToken ct) =>
        {
            var result = await supplierService.GetByIdAsync(id, ct);
            return result.Success
                ? Results.Ok(result)
                : Results.NotFound(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetSupplierById")
        .WithSummary("Retrieves a supplier profile by ID.");

        // Search Suppliers (Manager, Admin, Owner)
        group.MapGet("", async (
            string? searchTerm,
            bool? isActive,
            int? page,
            int? pageSize,
            ISupplierService supplierService,
            CancellationToken ct) =>
        {
            var result = await supplierService.SearchAsync(
                searchTerm,
                isActive,
                page ?? 1,
                pageSize ?? 20,
                ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("SearchSuppliers")
        .WithSummary("Searches suppliers with pagination.");

        // Deactivate Supplier (Admin, Owner only)
        group.MapDelete("/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            ISupplierService supplierService,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await supplierService.DeactivateAsync(id, username, ct);
            return result.Success
                ? Results.Ok(result)
                : (result.ErrorCode == "SUPPLIER_NOT_FOUND" ? Results.NotFound(result) : Results.BadRequest(result));
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("DeactivateSupplier")
        .WithSummary("Deactivates a supplier profile.");

        // Activate Supplier (Admin, Owner only)
        group.MapPost("/{id:guid}/activate", async (
            Guid id,
            HttpContext httpContext,
            ISupplierService supplierService,
            CancellationToken ct) =>
        {
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await supplierService.ActivateAsync(id, username, ct);
            return result.Success
                ? Results.Ok(result)
                : (result.ErrorCode == "SUPPLIER_NOT_FOUND" ? Results.NotFound(result) : Results.BadRequest(result));
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("ActivateSupplier")
        .WithSummary("Activates a previously deactivated supplier profile.");

        // Record Supplier Payment (Manager, Admin, Owner)
        group.MapPost("/{id:guid}/payments", async (
            Guid id,
            RecordSupplierPaymentRequest request,
            HttpContext httpContext,
            ISupplierService supplierService,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;

            var result = await supplierService.RecordPaymentAsync(id, request, userId, username, ct);
            return result.Success
                ? Results.Created($"/api/v1/suppliers/{id}/payments/{result.Data!.Id}", result)
                : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("RecordSupplierPayment")
        .WithSummary("Records a supplier payment with invoice-specific or FIFO auto-allocation.");

        // Get Supplier Payments (Manager, Admin, Owner)
        group.MapGet("/{id:guid}/payments", async (
            Guid id,
            ISupplierService supplierService,
            CancellationToken ct) =>
        {
            var result = await supplierService.GetPaymentsAsync(id, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetSupplierPayments")
        .WithSummary("Retrieves all payment transactions for a supplier.");

        return endpoints;
    }
}
