using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Application.Services;

public sealed class ProductService : IProductService
{
    private readonly IProductRepository _productRepo;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly ICategoryRepository _categoryRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public ProductService(
        IProductRepository productRepo,
        IInventoryRepository inventoryRepo,
        ICategoryRepository categoryRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _productRepo = productRepo;
        _inventoryRepo = inventoryRepo;
        _categoryRepo = categoryRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<ApiResponse<ProductManagementDto>> CreateProductAsync(
        CreateProductRequest request,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (request.InitialStock < 0m)
            throw new DomainException("Initial stock cannot be negative.", "INVALID_INITIAL_STOCK");

        if (request.ReorderLevel < 0m)
            throw new DomainException("Reorder level cannot be negative.", "INVALID_REORDER_LEVEL");

        var unit = UnitOfMeasure.FromCode(request.UnitCode);
        var initialQty = Quantity.From(request.InitialStock, unit);
        var reorderQty = Quantity.From(request.ReorderLevel, unit);

        // Validate foreign keys
        var taxCatExists = await _categoryRepo.TaxCategoryExistsAsync(request.TaxCategoryId, cancellationToken);
        if (!taxCatExists)
            throw new DomainException("Tax category not found.", "TAX_CATEGORY_NOT_FOUND");

        string? categoryName = null;
        if (request.CategoryId.HasValue)
        {
            var cat = await _categoryRepo.GetCategoryByIdAsync(request.CategoryId.Value, cancellationToken);
            if (cat == null)
                throw new DomainException("Category not found.", "CATEGORY_NOT_FOUND");
            categoryName = cat.Name;
        }

        string? brandName = null;
        if (request.BrandId.HasValue)
        {
            var brand = await _categoryRepo.GetBrandByIdAsync(request.BrandId.Value, cancellationToken);
            if (brand == null)
                throw new DomainException("Brand not found.", "BRAND_NOT_FOUND");
            brandName = brand.Name;
        }

        var normalizedSku = request.Sku.Trim().ToUpperInvariant();
        var existingProduct = await _productRepo.GetBySkuAsync(normalizedSku, cancellationToken);
        if (existingProduct != null)
            throw new DomainException($"Product with SKU '{normalizedSku}' already exists.", "SKU_ALREADY_EXISTS");

        string? primaryBarcode = null;
        if (!string.IsNullOrWhiteSpace(request.PrimaryBarcode))
        {
            primaryBarcode = request.PrimaryBarcode.Trim();
            if (primaryBarcode.Length > 64)
                throw new DomainException("Barcode cannot exceed 64 characters.", "INVALID_BARCODE");

            var hasBarcode = await _productRepo.HasBarcodeAsync(primaryBarcode, cancellationToken);
            if (hasBarcode)
                throw new DomainException($"Barcode '{primaryBarcode}' is already registered to another product.", "BARCODE_ALREADY_EXISTS");
        }

        var costPrice = Money.FromMinorUnits(request.CostPricePaise, Currency.INR);
        var sellingPrice = Money.FromMinorUnits(request.SellingPricePaise, Currency.INR);

        await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

        var product = new Product(
            Guid.NewGuid(),
            normalizedSku,
            request.Name,
            unit,
            costPrice,
            sellingPrice,
            request.TaxCategoryId,
            request.CategoryId,
            request.BrandId,
            request.Notes);

        if (primaryBarcode != null)
        {
            product.AddBarcode(new Barcode(primaryBarcode), isPrimary: true);
        }

        await _productRepo.AddAsync(product, cancellationToken);

        var stockBalance = new StockBalance(product.Id, unit, request.InitialStock, request.ReorderLevel);
        await _inventoryRepo.AddStockBalanceAsync(stockBalance, cancellationToken);

        if (request.InitialStock > 0m)
        {
            var movement = new StockMovement(
                id: 0,
                productId: product.Id,
                movementType: StockMovementType.AdjustmentIncrease,
                quantityChanged: initialQty,
                resultingQuantity: initialQty,
                referenceNumber: "INITIAL_STOCK",
                reason: "Initial stock upon product creation.",
                userId: null,
                terminalId: null);

            await _inventoryRepo.AddStockMovementAsync(movement, cancellationToken);
        }

        await _auditRepo.RecordAsync("Catalog", "PRODUCT_CREATED", executingUser, $"Created product {product.SKU} ({product.Name}).", cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var dto = MapToManagementDto(product, stockBalance, categoryName, brandName);
        return ApiResponse.Ok(dto);
    }

    public async Task<ApiResponse<ProductManagementDto>> UpdateProductAsync(
        Guid id,
        UpdateProductRequest request,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        var product = await _productRepo.GetByIdAsync(id, cancellationToken);
        if (product == null)
            throw new DomainException("Product not found.", "PRODUCT_NOT_FOUND");

        if (request.ReorderLevel < 0m)
            throw new DomainException("Reorder level cannot be negative.", "INVALID_REORDER_LEVEL");

                var newUnit = UnitOfMeasure.FromCode(request.UnitCode);
        var unitChanged = newUnit.Code != product.Unit.Code;

        // Enforce Unit-of-Measure Immutability if any historical usage exists
        if (newUnit.Code != product.Unit.Code)
        {
            var stock = await _inventoryRepo.GetStockBalanceAsync(product.Id, cancellationToken);
            var hasStock = stock != null && stock.CurrentQuantity.Value != 0m;
            var hasMovements = await _inventoryRepo.HasMovementsAsync(product.Id, cancellationToken);
            var hasSales = await _productRepo.HasSalesHistoryAsync(product.Id, cancellationToken);
            var hasPurchases = await _productRepo.HasPurchaseHistoryAsync(product.Id, cancellationToken);

            if (hasStock || hasMovements || hasSales || hasPurchases)
            {
                throw new DomainException(
                    $"Cannot modify unit of measure for product '{product.Name}' because historical inventory, movements, sales, or purchase records exist.",
                    "UNIT_IMMUTABLE");
            }
        }

        // Validate foreign keys
        var taxCatExists = await _categoryRepo.TaxCategoryExistsAsync(request.TaxCategoryId, cancellationToken);
        if (!taxCatExists)
            throw new DomainException("Tax category not found.", "TAX_CATEGORY_NOT_FOUND");

        string? categoryName = null;
        if (request.CategoryId.HasValue)
        {
            var cat = await _categoryRepo.GetCategoryByIdAsync(request.CategoryId.Value, cancellationToken);
            if (cat == null)
                throw new DomainException("Category not found.", "CATEGORY_NOT_FOUND");
            categoryName = cat.Name;
        }

        string? brandName = null;
        if (request.BrandId.HasValue)
        {
            var brand = await _categoryRepo.GetBrandByIdAsync(request.BrandId.Value, cancellationToken);
            if (brand == null)
                throw new DomainException("Brand not found.", "BRAND_NOT_FOUND");
            brandName = brand.Name;
        }

        var reorderQty = Quantity.From(request.ReorderLevel, newUnit);
        var costPrice = Money.FromMinorUnits(request.CostPricePaise, Currency.INR);
        var sellingPrice = Money.FromMinorUnits(request.SellingPricePaise, Currency.INR);

        await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

        product.UpdateDetails(request.Name, newUnit, request.TaxCategoryId, request.CategoryId, request.BrandId, request.Notes);
        product.UpdatePrices(costPrice, sellingPrice);

        var stockBalance = await _inventoryRepo.GetStockBalanceTrackedAsync(product.Id, cancellationToken);
        if (stockBalance != null)
        {
            if (unitChanged)
            {
                stockBalance.UpdateUnitOfMeasure(newUnit, reorderQty);
            }
            else
            {
                stockBalance.UpdateReorderLevel(reorderQty);
            }
        }

        await _auditRepo.RecordAsync("Catalog", "PRODUCT_UPDATED", executingUser, $"Updated product {product.SKU} ({product.Name}).", cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var dto = MapToManagementDto(product, stockBalance, categoryName, brandName);
        return ApiResponse.Ok(dto);
    }

    public async Task<ApiResponse<ProductBarcodeDto>> AddBarcodeAsync(
        Guid productId,
        AddBarcodeRequest request,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        var product = await _productRepo.GetByIdAsync(productId, cancellationToken);
        if (product == null)
            throw new DomainException("Product not found.", "PRODUCT_NOT_FOUND");

        if (string.IsNullOrWhiteSpace(request.Barcode))
            throw new DomainException("Barcode cannot be empty.", "INVALID_BARCODE");

        var normalizedBarcode = request.Barcode.Trim();
        if (normalizedBarcode.Length > 64)
            throw new DomainException("Barcode cannot exceed 64 characters.", "INVALID_BARCODE");

        var isTaken = await _productRepo.HasBarcodeAsync(normalizedBarcode, cancellationToken);
        if (isTaken)
            throw new DomainException($"Barcode '{normalizedBarcode}' is already registered to a product.", "BARCODE_ALREADY_EXISTS");

        await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

        product.AddBarcode(new Barcode(normalizedBarcode), request.IsPrimary);
        await _auditRepo.RecordAsync("Catalog", "BARCODE_ADDED", executingUser, $"Added barcode {normalizedBarcode} (Primary: {request.IsPrimary}) to product {product.SKU}.", cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var addedPb = product.Barcodes.First(b => b.Barcode.Value == normalizedBarcode);
        return ApiResponse.Ok(new ProductBarcodeDto(addedPb.Id, addedPb.Barcode.Value, addedPb.IsPrimary, addedPb.IsActive));
    }

    public async Task<ApiResponse<ProductPosDto>> GetProductPosByBarcodeAsync(string barcode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            throw new DomainException("Barcode cannot be empty.", "INVALID_BARCODE");

        var normalized = barcode.Trim();
        var product = await _productRepo.GetByBarcodeAsync(normalized, cancellationToken);
        if (product == null || !product.IsActive)
            return ApiResponse.Fail<ProductPosDto>("Product not found.", "PRODUCT_NOT_FOUND");

        var stock = await _inventoryRepo.GetStockBalanceAsync(product.Id, cancellationToken);
        var inStock = stock != null && stock.CurrentQuantity.Value > 0m;
        var primaryBarcode = product.Barcodes.FirstOrDefault(b => b.IsPrimary && b.IsActive)?.Barcode.Value
                             ?? product.Barcodes.FirstOrDefault(b => b.IsActive)?.Barcode.Value;

        var dto = new ProductPosDto(
            product.Id,
            product.SKU,
            product.Name,
            product.Unit.Code,
            product.SellingPrice.MinorUnits,
            product.TaxCategoryId,
            primaryBarcode,
            inStock,
            product.IsActive);

        return ApiResponse.Ok(dto);
    }

    public async Task<ApiResponse<ProductPosDto>> GetProductPosByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await _productRepo.GetByIdAsync(id, cancellationToken);
        if (product == null || !product.IsActive)
            return ApiResponse.Fail<ProductPosDto>("Product not found.", "PRODUCT_NOT_FOUND");

        var stock = await _inventoryRepo.GetStockBalanceAsync(product.Id, cancellationToken);
        var inStock = stock != null && stock.CurrentQuantity.Value > 0m;
        var primaryBarcode = product.Barcodes.FirstOrDefault(b => b.IsPrimary && b.IsActive)?.Barcode.Value
                             ?? product.Barcodes.FirstOrDefault(b => b.IsActive)?.Barcode.Value;

        var dto = new ProductPosDto(
            product.Id,
            product.SKU,
            product.Name,
            product.Unit.Code,
            product.SellingPrice.MinorUnits,
            product.TaxCategoryId,
            primaryBarcode,
            inStock,
            product.IsActive);

        return ApiResponse.Ok(dto);
    }

    public async Task<ApiResponse<PagedResult<ProductPosDto>>> SearchPosProductsAsync(
        string query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new DomainException("Search query cannot be empty.", "QUERY_REQUIRED");

        var trimmed = query.Trim();
        if (trimmed.Length > 100)
            throw new DomainException("Search query cannot exceed 100 characters.", "QUERY_TOO_LONG");

        var clampedPage = Math.Max(1, page);
        var clampedSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 50);

        var (products, totalCount) = await _productRepo.SearchPosAsync(trimmed, clampedPage, clampedSize, cancellationToken);

        var items = new List<ProductPosDto>(products.Count);
        foreach (var p in products)
        {
            var stock = await _inventoryRepo.GetStockBalanceAsync(p.Id, cancellationToken);
            var inStock = stock != null && stock.CurrentQuantity.Value > 0m;
            var primaryBarcode = p.Barcodes.FirstOrDefault(b => b.IsPrimary && b.IsActive)?.Barcode.Value
                                 ?? p.Barcodes.FirstOrDefault(b => b.IsActive)?.Barcode.Value;

            items.Add(new ProductPosDto(
                p.Id,
                p.SKU,
                p.Name,
                p.Unit.Code,
                p.SellingPrice.MinorUnits,
                p.TaxCategoryId,
                primaryBarcode,
                inStock,
                p.IsActive));
        }

        var result = new PagedResult<ProductPosDto>(items, totalCount, clampedPage, clampedSize);
        return ApiResponse.Ok(result);
    }

    public async Task<ApiResponse<ProductManagementDto>> GetProductManagementByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await _productRepo.GetByIdAsync(id, cancellationToken);
        if (product == null)
            return ApiResponse.Fail<ProductManagementDto>("Product not found.", "PRODUCT_NOT_FOUND");

        var stock = await _inventoryRepo.GetStockBalanceAsync(product.Id, cancellationToken);
        string? categoryName = null;
        if (product.CategoryId.HasValue)
        {
            var cat = await _categoryRepo.GetCategoryByIdAsync(product.CategoryId.Value, cancellationToken);
            categoryName = cat?.Name;
        }

        string? brandName = null;
        if (product.BrandId.HasValue)
        {
            var b = await _categoryRepo.GetBrandByIdAsync(product.BrandId.Value, cancellationToken);
            brandName = b?.Name;
        }

        var dto = MapToManagementDto(product, stock, categoryName, brandName);
        return ApiResponse.Ok(dto);
    }

    public async Task<ApiResponse<PagedResult<ProductManagementDto>>> GetProductsManagementAsync(
        int? categoryId,
        int? brandId,
        bool? activeOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var clampedPage = Math.Max(1, page);
        var clampedSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 50);

        var (products, totalCount) = await _productRepo.GetAllManagementAsync(categoryId, brandId, activeOnly, clampedPage, clampedSize, cancellationToken);

        var items = new List<ProductManagementDto>(products.Count);
        foreach (var p in products)
        {
            var stock = await _inventoryRepo.GetStockBalanceAsync(p.Id, cancellationToken);
            string? catName = null;
            if (p.CategoryId.HasValue)
            {
                var c = await _categoryRepo.GetCategoryByIdAsync(p.CategoryId.Value, cancellationToken);
                catName = c?.Name;
            }

            string? bName = null;
            if (p.BrandId.HasValue)
            {
                var b = await _categoryRepo.GetBrandByIdAsync(p.BrandId.Value, cancellationToken);
                bName = b?.Name;
            }

            items.Add(MapToManagementDto(p, stock, catName, bName));
        }

        var result = new PagedResult<ProductManagementDto>(items, totalCount, clampedPage, clampedSize);
        return ApiResponse.Ok(result);
    }

    public async Task<ApiResponse<ProductManagementDto>> SetProductStatusAsync(
        Guid id,
        bool isActive,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        var product = await _productRepo.GetByIdAsync(id, cancellationToken);
        if (product == null)
            throw new DomainException("Product not found.", "PRODUCT_NOT_FOUND");

        await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

        if (isActive)
            product.Activate();
        else
            product.Deactivate();

        var action = isActive ? "PRODUCT_ACTIVATED" : "PRODUCT_DEACTIVATED";
        await _auditRepo.RecordAsync("Catalog", action, executingUser, $"Set product {product.SKU} status to {(isActive ? "Active" : "Inactive")}.", cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var stock = await _inventoryRepo.GetStockBalanceAsync(product.Id, cancellationToken);
        var dto = MapToManagementDto(product, stock, null, null);
        return ApiResponse.Ok(dto);
    }

    private static ProductManagementDto MapToManagementDto(
        Product p,
        StockBalance? stock,
        string? categoryName,
        string? brandName)
    {
        var barcodes = p.Barcodes.Select(b => new ProductBarcodeDto(b.Id, b.Barcode.Value, b.IsPrimary, b.IsActive)).ToList();
        return new ProductManagementDto(
            p.Id,
            p.SKU,
            p.Name,
            p.CategoryId,
            categoryName,
            p.BrandId,
            brandName,
            p.Unit.Code,
            p.CostPrice.MinorUnits,
            p.SellingPrice.MinorUnits,
            p.TaxCategoryId,
            p.IsActive,
            p.Notes,
            barcodes,
            stock?.CurrentQuantity.Value ?? 0m,
            stock?.ReorderLevel.Value ?? 0m,
            stock?.IsLowStock ?? false);
    }
}
