using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Customers;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/customers");

        group.MapGet("/", async (
            string? searchTerm,
            bool? isActive,
            int? page,
            int? pageSize,
            ICustomerService service,
            CancellationToken ct) =>
        {
            var result = await service.SearchAsync(searchTerm, isActive, page ?? 1, pageSize ?? 20, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapPost("/", async (
            CreateCustomerRequest request,
            ICustomerService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await service.CreateCustomerAsync(request, userId, username, ct);
            return result.Success ? Results.Created($"/api/v1/customers/{result.Data!.Id}", result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapGet("/{id:guid}", async (
            Guid id,
            ICustomerService service,
            CancellationToken ct) =>
        {
            var result = await service.GetByIdAsync(id, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapGet("/by-phone/{phone}", async (
            string phone,
            ICustomerService service,
            CancellationToken ct) =>
        {
            var result = await service.GetByPhoneAsync(phone, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateCustomerRequest request,
            ICustomerService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await service.UpdateCustomerAsync(id, request, userId, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapPut("/{id:guid}/credit-limit", async (
            Guid id,
            UpdateCreditLimitRequest request,
            ICustomerService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await service.UpdateCreditLimitAsync(id, request, userId, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapPut("/{id:guid}/status", async (
            Guid id,
            SetCustomerStatusRequest request,
            ICustomerService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var result = await service.SetStatusAsync(id, request, userId, username, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapPost("/{id:guid}/payments", async (
            Guid id,
            RecordCustomerPaymentRequest request,
            ICustomerService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = (Guid)httpContext.Items["CurrentUserId"]!;
            var username = (string)httpContext.Items["CurrentUsername"]!;
            var terminalId = httpContext.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var tidObj) && tidObj is Guid tid
                ? tid
                : (httpContext.Items.TryGetValue("CurrentSessionTerminalId", out var sidObj) && sidObj is Guid sid ? sid : Guid.Empty);

            var result = await service.RecordPaymentAsync(id, request, userId, username, terminalId, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapGet("/{id:guid}/payments", async (
            Guid id,
            ICustomerService service,
            CancellationToken ct) =>
        {
            var result = await service.GetPaymentsAsync(id, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapGet("/{id:guid}/ledger", async (
            Guid id,
            ICustomerService service,
            CancellationToken ct) =>
        {
            var result = await service.GetLedgerAsync(id, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapGet("/{id:guid}/sales", async (
            Guid id,
            ICustomerService service,
            CancellationToken ct) =>
        {
            var result = await service.GetSalesAsync(id, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner);
    }
}
