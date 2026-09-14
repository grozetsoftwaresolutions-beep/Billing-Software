using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Taxes;

namespace RetailPOS.Infrastructure.Persistence.Repositories;

public sealed class EfProductRepository : IProductRepository
{
    private readonly RetailDbContext _context;

    public EfProductRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Products
            .Include(p => p.Barcodes)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<Product?> GetBySkuAsync(string sku, CancellationToken cancellationToken = default)
    {
        var normalized = sku.Trim().ToUpperInvariant();
        return await _context.Products
            .Include(p => p.Barcodes)
            .FirstOrDefaultAsync(p => p.SKU == normalized, cancellationToken);
    }

    public async Task<Product?> GetByBarcodeAsync(string barcode, CancellationToken cancellationToken = default)
    {
        var normalized = barcode.Trim();
        var pb = await _context.ProductBarcodes
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Barcode == normalized && b.IsActive, cancellationToken);

        if (pb == null)
            return null;

        return await _context.Products
            .Include(p => p.Barcodes)
            .FirstOrDefaultAsync(p => p.Id == pb.ProductId, cancellationToken);
    }

    public async Task<(IReadOnlyList<Product> Items, int TotalCount)> SearchPosAsync(
        string query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Trim();
        var pattern = $"%{normalized}%";

        var baseQuery = _context.Products
            .Include(p => p.Barcodes)
            .Where(p => p.IsActive && (
                EF.Functions.Like(p.SKU, pattern) ||
                EF.Functions.Like(p.Name, pattern) ||
                p.Barcodes.Any(b => b.Barcode == normalized && b.IsActive)));

        var totalCount = await baseQuery.CountAsync(cancellationToken);

        var items = await baseQuery
            .OrderBy(p => p.Name)
            .ThenBy(p => p.SKU)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<(IReadOnlyList<Product> Items, int TotalCount)> GetAllManagementAsync(
        int? categoryId,
        int? brandId,
        bool? activeOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Products
            .Include(p => p.Barcodes)
            .AsQueryable();

        if (categoryId.HasValue)
            query = query.Where(p => p.CategoryId == categoryId.Value);

        if (brandId.HasValue)
            query = query.Where(p => p.BrandId == brandId.Value);

        if (activeOnly.HasValue)
            query = query.Where(p => p.IsActive == activeOnly.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(p => p.Name)
            .ThenBy(p => p.SKU)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Product product, CancellationToken cancellationToken = default)
    {
        await _context.Products.AddAsync(product, cancellationToken);
    }

    public async Task<bool> HasBarcodeAsync(string barcode, CancellationToken cancellationToken = default)
    {
        var normalized = barcode.Trim();
        return await _context.ProductBarcodes.AnyAsync(b => b.Barcode == normalized && b.IsActive, cancellationToken);
    }

    public async Task<bool> HasSalesHistoryAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        return await _context.SalesItems.AnyAsync(si => si.ProductId == productId, cancellationToken);
    }

    public async Task<bool> HasPurchaseHistoryAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        return await _context.PurchaseItems.AnyAsync(pi => pi.ProductId == productId, cancellationToken);
    }
}

public sealed class EfInventoryRepository : IInventoryRepository
{
    private readonly RetailDbContext _context;

    public EfInventoryRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<StockBalance?> GetStockBalanceAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        return await _context.StockBalances
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProductId == productId, cancellationToken);
    }

    public async Task<StockBalance?> GetStockBalanceTrackedAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        return await _context.StockBalances
            .FirstOrDefaultAsync(s => s.ProductId == productId, cancellationToken);
    }

    public async Task AddStockBalanceAsync(StockBalance balance, CancellationToken cancellationToken = default)
    {
        await _context.StockBalances.AddAsync(balance, cancellationToken);
    }

    public async Task<(IReadOnlyList<StockBalance> Items, int TotalCount)> GetLowStockAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _context.StockBalances
            .AsNoTracking()
            .Where(s => s.IsLowStock);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(s => s.ProductId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddStockMovementAsync(StockMovement movement, CancellationToken cancellationToken = default)
    {
        await _context.StockMovements.AddAsync(movement, cancellationToken);
    }

    public async Task<(IReadOnlyList<StockMovement> Items, int TotalCount)> GetStockMovementsAsync(
        Guid? productId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _context.StockMovements.AsNoTracking().AsQueryable();

        if (productId.HasValue)
            query = query.Where(m => m.ProductId == productId.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(m => m.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<bool> HasMovementsAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        return await _context.StockMovements.AnyAsync(m => m.ProductId == productId, cancellationToken);
    }
}

public sealed class EfCategoryRepository : ICategoryRepository
{
    private readonly RetailDbContext _context;

    public EfCategoryRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Category>> GetAllCategoriesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Categories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Category?> GetCategoryByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Category?> GetCategoryByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        return await _context.Categories.FirstOrDefaultAsync(c => c.Name == trimmed, cancellationToken);
    }

    public async Task AddCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        await _context.Categories.AddAsync(category, cancellationToken);
    }

    public async Task<IReadOnlyList<Brand>> GetAllBrandsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Brands
            .AsNoTracking()
            .OrderBy(b => b.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Brand?> GetBrandByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Brands.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public async Task<Brand?> GetBrandByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        return await _context.Brands.FirstOrDefaultAsync(b => b.Name == trimmed, cancellationToken);
    }

    public async Task AddBrandAsync(Brand brand, CancellationToken cancellationToken = default)
    {
        await _context.Brands.AddAsync(brand, cancellationToken);
    }

    public async Task<bool> TaxCategoryExistsAsync(int taxCategoryId, CancellationToken cancellationToken = default)
    {
        return await _context.TaxCategories.AnyAsync(t => t.Id == taxCategoryId && t.IsActive, cancellationToken);
    }

    public async Task<TaxCategory?> GetTaxCategoryByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.TaxCategories
            .Include(t => t.Components)
            .FirstOrDefaultAsync(t => t.Id == id && t.IsActive, cancellationToken);
    }
}
