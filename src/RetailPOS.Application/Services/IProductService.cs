using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Application.Services;

public interface IProductService
{
    Task<ApiResponse<ProductManagementDto>> CreateProductAsync(CreateProductRequest request, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<ProductManagementDto>> UpdateProductAsync(Guid id, UpdateProductRequest request, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<ProductBarcodeDto>> AddBarcodeAsync(Guid productId, AddBarcodeRequest request, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<ProductPosDto>> GetProductPosByBarcodeAsync(string barcode, CancellationToken cancellationToken = default);
    Task<ApiResponse<ProductPosDto>> GetProductPosByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ApiResponse<PagedResult<ProductPosDto>>> SearchPosProductsAsync(string query, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<ApiResponse<ProductManagementDto>> GetProductManagementByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ApiResponse<PagedResult<ProductManagementDto>>> GetProductsManagementAsync(int? categoryId, int? brandId, bool? activeOnly, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<ApiResponse<ProductManagementDto>> SetProductStatusAsync(Guid id, bool isActive, string executingUser, CancellationToken cancellationToken = default);
}
