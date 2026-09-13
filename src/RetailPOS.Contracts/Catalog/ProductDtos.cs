namespace RetailPOS.Contracts.Catalog;

public sealed record ProductPosDto(
    Guid Id,
    string Sku,
    string Name,
    string UnitCode,
    long SellingPricePaise,
    int TaxCategoryId,
    string? PrimaryBarcode,
    bool InStock,
    bool IsActive);

public sealed record ProductManagementDto(
    Guid Id,
    string Sku,
    string Name,
    int? CategoryId,
    string? CategoryName,
    int? BrandId,
    string? BrandName,
    string UnitCode,
    long CostPricePaise,
    long SellingPricePaise,
    int TaxCategoryId,
    bool IsActive,
    string? Notes,
    IReadOnlyList<ProductBarcodeDto> Barcodes,
    decimal CurrentStock,
    decimal ReorderLevel,
    bool IsLowStock);

public sealed record CreateProductRequest(
    string Sku,
    string Name,
    string UnitCode,
    long CostPricePaise,
    long SellingPricePaise,
    int TaxCategoryId,
    int? CategoryId = null,
    int? BrandId = null,
    string? PrimaryBarcode = null,
    decimal InitialStock = 0m,
    decimal ReorderLevel = 5m,
    string? Notes = null);

public sealed record UpdateProductRequest(
    string Name,
    string UnitCode,
    long CostPricePaise,
    long SellingPricePaise,
    int TaxCategoryId,
    int? CategoryId = null,
    int? BrandId = null,
    decimal ReorderLevel = 5m,
    string? Notes = null);

public sealed record AddBarcodeRequest(
    string Barcode,
    bool IsPrimary = false);

public sealed record ProductBarcodeDto(
    int Id,
    string Barcode,
    bool IsPrimary,
    bool IsActive);

public sealed record CategoryDto(
    int Id,
    string Name,
    string? Description,
    bool IsActive);

public sealed record CreateCategoryRequest(
    string Name,
    string? Description = null);

public sealed record BrandDto(
    int Id,
    string Name,
    bool IsActive);

public sealed record CreateBrandRequest(
    string Name);
