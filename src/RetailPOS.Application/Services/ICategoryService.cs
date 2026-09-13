using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Application.Services;

public interface ICategoryService
{
    Task<ApiResponse<IReadOnlyList<CategoryDto>>> GetAllCategoriesAsync(CancellationToken cancellationToken = default);
    Task<ApiResponse<CategoryDto>> CreateCategoryAsync(CreateCategoryRequest request, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<IReadOnlyList<BrandDto>>> GetAllBrandsAsync(CancellationToken cancellationToken = default);
    Task<ApiResponse<BrandDto>> CreateBrandAsync(CreateBrandRequest request, string executingUser, CancellationToken cancellationToken = default);
}
