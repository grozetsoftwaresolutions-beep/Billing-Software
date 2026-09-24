using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetailPOS.Client.Services;
using RetailPOS.Contracts.Catalog;

namespace RetailPOS.Client.ViewModels;

public sealed partial class ProductsViewModel : ObservableObject
{
    private readonly ProductApiClient _productClient;
    private readonly InventoryApiClient _inventoryClient;

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; } // 0 = Catalog, 1 = Inventory & Stock

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SuccessMessage { get; set; } = string.Empty;

    // --- Catalog Filter & Pagination State ---
    [ObservableProperty]
    public partial int? SelectedCategoryId { get; set; }

    [ObservableProperty]
    public partial int? SelectedBrandId { get; set; }

    [ObservableProperty]
    public partial bool? ActiveOnlyFilter { get; set; }

    [ObservableProperty]
    public partial int Page { get; set; } = 1;

    [ObservableProperty]
    public partial int PageSize { get; set; } = 20;

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    [ObservableProperty]
    public partial int TotalPages { get; set; } = 1;

    [ObservableProperty]
    public partial ProductItemViewModel? SelectedProduct { get; set; }

    public ObservableCollection<ProductItemViewModel> Products { get; } = [];
    public ObservableCollection<CategoryDto> Categories { get; } = [];
    public ObservableCollection<BrandDto> Brands { get; } = [];

    // --- Child ViewModels ---
    public ProductEditViewModel EditViewModel { get; } = new();
    public InventoryViewModel InventoryVm { get; }

    public ProductsViewModel(ProductApiClient? productClient = null, InventoryApiClient? inventoryClient = null)
    {
        _productClient = productClient ?? new ProductApiClient();
        _inventoryClient = inventoryClient ?? new InventoryApiClient();
        InventoryVm = new InventoryViewModel(_inventoryClient);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await LoadCatalogFiltersAsync(ct);
        await LoadProductsAsync(ct);
        await InventoryVm.LoadStockAsync(ct);
    }

    public async Task LoadCatalogFiltersAsync(CancellationToken ct = default)
    {
        try
        {
            var catRes = await _productClient.GetCategoriesAsync(ct: ct);
            if (catRes.Success && catRes.Data != null)
            {
                Categories.Clear();
                foreach (var c in catRes.Data)
                {
                    Categories.Add(c);
                }
            }

            var brandRes = await _productClient.GetBrandsAsync(ct: ct);
            if (brandRes.Success && brandRes.Data != null)
            {
                Brands.Clear();
                foreach (var b in brandRes.Data)
                {
                    Brands.Add(b);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unable to load filters: {ex.Message}";
        }
    }

    public async Task LoadProductsAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var res = await _productClient.GetProductsManagementAsync(
                SelectedCategoryId,
                SelectedBrandId,
                ActiveOnlyFilter,
                Page,
                PageSize,
                ct: ct);

            if (res.Success && res.Data != null)
            {
                Products.Clear();
                foreach (var dto in res.Data.Items)
                {
                    Products.Add(new ProductItemViewModel(dto));
                }

                TotalCount = res.Data.TotalCount;
                TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to load products from server.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Catalog error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool?> PrepareProductForEditAsync(ProductManagementDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        bool hasTransactionHistory = false;
        bool lookupFailed = false;
        try
        {
            // Authoritative server query: checks movement audit ledger (sales, purchases, adjustments, wastage)
            var movementsRes = await _inventoryClient.GetStockMovementsAsync(dto.Id, page: 1, pageSize: 1, ct: ct);
            if (movementsRes.Success && movementsRes.Data != null)
            {
                hasTransactionHistory = movementsRes.Data.TotalCount > 0;
            }
            else
            {
                lookupFailed = true;
            }
        }
        catch
        {
            // IMPORTANT: If history lookup fails, do NOT interpret as "no history".
            // Mark lookup as failed so the client does not safely present the Unit as editable.
            lookupFailed = true;
        }

        EditViewModel.InitializeForEdit(dto, hasTransactionHistory, lookupFailed);
        return lookupFailed ? null : hasTransactionHistory;
    }

    public async Task<bool> SaveProductAsync(CancellationToken ct = default)
    {
        if (!EditViewModel.Validate(out var error))
        {
            ErrorMessage = error;
            return false;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            if (EditViewModel.IsCreateMode)
            {
                var req = EditViewModel.ToCreateRequest();
                var res = await _productClient.CreateProductAsync(req, ct: ct);
                if (!res.Success)
                {
                    ErrorMessage = res.ErrorMessage ?? "Failed to create product.";
                    return false;
                }

                SuccessMessage = $"Product '{req.Sku}' created successfully.";
            }
            else
            {
                var req = EditViewModel.ToUpdateRequest();
                var res = await _productClient.UpdateProductAsync(EditViewModel.Id, req, ct: ct);
                if (!res.Success)
                {
                    ErrorMessage = res.ErrorMessage ?? "Failed to update product.";
                    return false;
                }

                SuccessMessage = $"Product '{EditViewModel.Sku}' updated successfully.";
            }

            // Authoritative server re-fetch after mutation
            await LoadProductsAsync(ct);
            await InventoryVm.LoadStockAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Save error: {ex.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> AddBarcodeToProductAsync(Guid productId, string barcode, bool isPrimary, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            ErrorMessage = "Barcode cannot be empty.";
            return false;
        }

        IsLoading = true;
        try
        {
            var req = new AddBarcodeRequest(barcode.Trim(), isPrimary);
            var res = await _productClient.AddBarcodeAsync(productId, req, ct: ct);
            if (!res.Success)
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to add barcode.";
                return false;
            }

            SuccessMessage = $"Barcode '{barcode.Trim()}' added successfully.";
            await LoadProductsAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Add barcode error: {ex.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> ToggleProductStatusAsync(ProductItemViewModel item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        IsLoading = true;
        try
        {
            var newStatus = !item.IsActive;
            var res = await _productClient.SetProductStatusAsync(item.Id, newStatus, ct: ct);
            if (!res.Success)
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to update product status.";
                return false;
            }

            SuccessMessage = $"Product '{item.Sku}' status set to {(newStatus ? "Active" : "Inactive")}.";
            await LoadProductsAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Status update error: {ex.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> CreateCategoryAsync(string name, string? description = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Category name cannot be empty.";
            return false;
        }

        try
        {
            var req = new CreateCategoryRequest(name.Trim(), description?.Trim());
            var res = await _productClient.CreateCategoryAsync(req, ct: ct);
            if (res.Success)
            {
                await LoadCatalogFiltersAsync(ct);
                return true;
            }
            ErrorMessage = res.ErrorMessage ?? "Failed to create category.";
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Create category error: {ex.Message}";
            return false;
        }
    }

    public async Task<bool> CreateBrandAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Brand name cannot be empty.";
            return false;
        }

        try
        {
            var req = new CreateBrandRequest(name.Trim());
            var res = await _productClient.CreateBrandAsync(req, ct: ct);
            if (res.Success)
            {
                await LoadCatalogFiltersAsync(ct);
                return true;
            }
            ErrorMessage = res.ErrorMessage ?? "Failed to create brand.";
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Create brand error: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    public async Task NextPageAsync()
    {
        if (Page < TotalPages)
        {
            Page++;
            await LoadProductsAsync();
        }
    }

    [RelayCommand]
    public async Task PreviousPageAsync()
    {
        if (Page > 1)
        {
            Page--;
            await LoadProductsAsync();
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await LoadProductsAsync();
        await InventoryVm.LoadStockAsync();
        await InventoryVm.LoadMovementsAsync();
    }
}
