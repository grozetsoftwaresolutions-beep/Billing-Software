using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;

namespace RetailPOS.Application.Services;

public sealed class CategoryService : ICategoryService
{
    private readonly ICategoryRepository _categoryRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public CategoryService(
        ICategoryRepository categoryRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _categoryRepo = categoryRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<ApiResponse<IReadOnlyList<CategoryDto>>> GetAllCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _categoryRepo.GetAllCategoriesAsync(cancellationToken);
        var dtos = categories.Select(c => new CategoryDto(c.Id, c.Name, c.Description, c.IsActive)).ToList();
        return ApiResponse.Ok<IReadOnlyList<CategoryDto>>(dtos);
    }

    public async Task<ApiResponse<CategoryDto>> CreateCategoryAsync(
        CreateCategoryRequest request,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new DomainException("Category name cannot be empty.", "INVALID_CATEGORY_NAME");

        var trimmed = request.Name.Trim();
        var existing = await _categoryRepo.GetCategoryByNameAsync(trimmed, cancellationToken);
        if (existing != null)
            throw new DomainException($"Category '{trimmed}' already exists.", "CATEGORY_ALREADY_EXISTS");

        var category = new Category(0, trimmed, request.Description?.Trim());
        await _categoryRepo.AddCategoryAsync(category, cancellationToken);
        await _auditRepo.RecordAsync("Catalog", "CATEGORY_CREATED", executingUser, $"Created category {trimmed}.", cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return ApiResponse.Ok(new CategoryDto(category.Id, category.Name, category.Description, category.IsActive));
    }

    public async Task<ApiResponse<IReadOnlyList<BrandDto>>> GetAllBrandsAsync(CancellationToken cancellationToken = default)
    {
        var brands = await _categoryRepo.GetAllBrandsAsync(cancellationToken);
        var dtos = brands.Select(b => new BrandDto(b.Id, b.Name, b.IsActive)).ToList();
        return ApiResponse.Ok<IReadOnlyList<BrandDto>>(dtos);
    }

    public async Task<ApiResponse<BrandDto>> CreateBrandAsync(
        CreateBrandRequest request,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new DomainException("Brand name cannot be empty.", "INVALID_BRAND_NAME");

        var trimmed = request.Name.Trim();
        var existing = await _categoryRepo.GetBrandByNameAsync(trimmed, cancellationToken);
        if (existing != null)
            throw new DomainException($"Brand '{trimmed}' already exists.", "BRAND_ALREADY_EXISTS");

        var brand = new Brand(0, trimmed);
        await _categoryRepo.AddBrandAsync(brand, cancellationToken);
        await _auditRepo.RecordAsync("Catalog", "BRAND_CREATED", executingUser, $"Created brand {trimmed}.", cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return ApiResponse.Ok(new BrandDto(brand.Id, brand.Name, brand.IsActive));
    }
}
