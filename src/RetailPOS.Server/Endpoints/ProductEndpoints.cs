using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/products");

        // POS Fast Barcode Resolution (Cashier Safe)
        group.MapGet("/barcode/{barcode}", async (
            string barcode,
            IProductService productService,
            CancellationToken ct) =>
        {
            var result = await productService.GetProductPosByBarcodeAsync(barcode, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetProductPosByBarcode")
        .WithSummary("Resolves product by barcode for POS billing.");

        // POS Search (Cashier Safe, Bounded)
        group.MapGet("/search", async (
            string? query,
            int? page,
            int? pageSize,
            IProductService productService,
            CancellationToken ct) =>
        {
            var result = await productService.SearchPosProductsAsync(query ?? string.Empty, page ?? 1, pageSize ?? 20, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("SearchPosProducts")
        .WithSummary("Fast bounded product search for POS checkout.");

        // POS Lookup by Id (Cashier Safe)
        group.MapGet("/pos/{id:guid}", async (
            Guid id,
            IProductService productService,
            CancellationToken ct) =>
        {
            var result = await productService.GetProductPosByIdAsync(id, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetProductPosById")
        .WithSummary("Gets cashier-safe product details by ID.");

        // Management: Get Product with full inventory & cost data
        group.MapGet("/{id:guid}", async (
            Guid id,
            IProductService productService,
            CancellationToken ct) =>
        {
            var result = await productService.GetProductManagementByIdAsync(id, ct);
            return result.Success ? Results.Ok(result) : Results.NotFound(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetProductManagementById")
        .WithSummary("Gets full product catalog and inventory details.");

        // Management: Paginated full product listing
        group.MapGet("/", async (
            int? categoryId,
            int? brandId,
            bool? activeOnly,
            int? page,
            int? pageSize,
            IProductService productService,
            CancellationToken ct) =>
        {
            var result = await productService.GetProductsManagementAsync(categoryId, brandId, activeOnly, page ?? 1, pageSize ?? 20, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetProductsManagement")
        .WithSummary("Lists products for catalog management.");

        // Management: Create Product
        group.MapPost("/", async (
            CreateProductRequest request,
            HttpContext context,
            IProductService productService,
            CancellationToken ct) =>
        {
            var username = (string)context.Items["CurrentUsername"]!;
            var result = await productService.CreateProductAsync(request, username, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("CreateProduct")
        .WithSummary("Creates a new product with initial stock balance.");

        // Management: Update Product Details & Prices
        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateProductRequest request,
            HttpContext context,
            IProductService productService,
            CancellationToken ct) =>
        {
            var username = (string)context.Items["CurrentUsername"]!;
            var result = await productService.UpdateProductAsync(id, request, username, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("UpdateProduct")
        .WithSummary("Updates product details, prices, and reorder level.");

        // Management: Add Barcode
        group.MapPost("/{id:guid}/barcodes", async (
            Guid id,
            AddBarcodeRequest request,
            HttpContext context,
            IProductService productService,
            CancellationToken ct) =>
        {
            var username = (string)context.Items["CurrentUsername"]!;
            var result = await productService.AddBarcodeAsync(id, request, username, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("AddProductBarcode")
        .WithSummary("Registers an additional barcode for a product.");

        // Management: Activate/Deactivate
        group.MapPut("/{id:guid}/status", async (
            Guid id,
            bool isActive,
            HttpContext context,
            IProductService productService,
            CancellationToken ct) =>
        {
            var username = (string)context.Items["CurrentUsername"]!;
            var result = await productService.SetProductStatusAsync(id, isActive, username, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("SetProductStatus")
        .WithSummary("Activates or deactivates a product.");

        return endpoints;
    }
}
