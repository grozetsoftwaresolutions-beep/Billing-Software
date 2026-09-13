using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var categoryGroup = endpoints.MapGroup("/api/v1/categories");

        categoryGroup.MapGet("/", async (
            ICategoryService categoryService,
            CancellationToken ct) =>
        {
            var result = await categoryService.GetAllCategoriesAsync(ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetCategories")
        .WithSummary("Lists active categories.");

        categoryGroup.MapPost("/", async (
            CreateCategoryRequest request,
            HttpContext context,
            ICategoryService categoryService,
            CancellationToken ct) =>
        {
            var username = (string)context.Items["CurrentUsername"]!;
            var result = await categoryService.CreateCategoryAsync(request, username, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("CreateCategory")
        .WithSummary("Creates a new product category.");

        var brandGroup = endpoints.MapGroup("/api/v1/brands");

        brandGroup.MapGet("/", async (
            ICategoryService categoryService,
            CancellationToken ct) =>
        {
            var result = await categoryService.GetAllBrandsAsync(ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Cashier, RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetBrands")
        .WithSummary("Lists active brands.");

        brandGroup.MapPost("/", async (
            CreateBrandRequest request,
            HttpContext context,
            ICategoryService categoryService,
            CancellationToken ct) =>
        {
            var username = (string)context.Items["CurrentUsername"]!;
            var result = await categoryService.CreateBrandAsync(request, username, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("CreateBrand")
        .WithSummary("Creates a new product brand.");

        return endpoints;
    }
}
