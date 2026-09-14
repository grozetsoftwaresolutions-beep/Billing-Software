using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Taxes;

namespace RetailPOS.Application.Persistence;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Product?> GetBySkuAsync(string sku, CancellationToken cancellationToken = default);
    Task<Product?> GetByBarcodeAsync(string barcode, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<Product> Items, int TotalCount)> SearchPosAsync(string query, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<Product> Items, int TotalCount)> GetAllManagementAsync(int? categoryId, int? brandId, bool? activeOnly, int page, int pageSize, CancellationToken cancellationToken = default);
    Task AddAsync(Product product, CancellationToken cancellationToken = default);
    Task<bool> HasBarcodeAsync(string barcode, CancellationToken cancellationToken = default);
    Task<bool> HasSalesHistoryAsync(Guid productId, CancellationToken cancellationToken = default);
    Task<bool> HasPurchaseHistoryAsync(Guid productId, CancellationToken cancellationToken = default);
}

public interface IInventoryRepository
{
    Task<StockBalance?> GetStockBalanceAsync(Guid productId, CancellationToken cancellationToken = default);
    Task<StockBalance?> GetStockBalanceTrackedAsync(Guid productId, CancellationToken cancellationToken = default);
    Task AddStockBalanceAsync(StockBalance balance, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<StockBalance> Items, int TotalCount)> GetLowStockAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task AddStockMovementAsync(StockMovement movement, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<StockMovement> Items, int TotalCount)> GetStockMovementsAsync(Guid? productId, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<bool> HasMovementsAsync(Guid productId, CancellationToken cancellationToken = default);
}

public interface ICategoryRepository
{
    Task<IReadOnlyList<Category>> GetAllCategoriesAsync(CancellationToken cancellationToken = default);
    Task<Category?> GetCategoryByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<Category?> GetCategoryByNameAsync(string name, CancellationToken cancellationToken = default);
    Task AddCategoryAsync(Category category, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Brand>> GetAllBrandsAsync(CancellationToken cancellationToken = default);
    Task<Brand?> GetBrandByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<Brand?> GetBrandByNameAsync(string name, CancellationToken cancellationToken = default);
    Task AddBrandAsync(Brand brand, CancellationToken cancellationToken = default);
    Task<bool> TaxCategoryExistsAsync(int taxCategoryId, CancellationToken cancellationToken = default);
    Task<TaxCategory?> GetTaxCategoryByIdAsync(int id, CancellationToken cancellationToken = default);
}
