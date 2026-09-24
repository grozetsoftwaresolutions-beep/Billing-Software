using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Client.Services;
using RetailPOS.Contracts.Inventory;

namespace RetailPOS.Client.ViewModels;

public sealed partial class InventoryViewModel : ObservableObject
{
    private readonly InventoryApiClient _inventoryClient;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LowStockOnly { get; set; }

    [ObservableProperty]
    public partial int Page { get; set; } = 1;

    [ObservableProperty]
    public partial int PageSize { get; set; } = 20;

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    [ObservableProperty]
    public partial int TotalPages { get; set; } = 1;

    [ObservableProperty]
    public partial int AuthoritativeLowStockCount { get; set; }

    public ObservableCollection<StockBalanceDto> StockItems { get; } = [];

    // --- Stock Adjustment Dialog State ---
    [ObservableProperty]
    public partial Guid AdjustmentProductId { get; set; } = Guid.Empty;

    [ObservableProperty]
    public partial string AdjustmentSku { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AdjustmentProductName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedCurrentStock))]
    [NotifyPropertyChangedFor(nameof(FormattedProjectedStock))]
    public partial string AdjustmentUnitCode { get; set; } = "PC";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedCurrentStock))]
    [NotifyPropertyChangedFor(nameof(FormattedProjectedStock))]
    public partial decimal AdjustmentCurrentStock { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedProjectedStock))]
    public partial ManualStockAdjustmentType SelectedAdjustmentType { get; set; } = ManualStockAdjustmentType.Increase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedProjectedStock))]
    public partial string AdjustmentQuantityText { get; set; } = "1";

    [ObservableProperty]
    public partial string AdjustmentReasonText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AdjustmentErrorMessage { get; set; } = string.Empty;

    public string FormattedCurrentStock => ProductItemViewModel.FormatQuantity(AdjustmentCurrentStock, AdjustmentUnitCode);

    public string FormattedProjectedStock
    {
        get
        {
            if (decimal.TryParse(AdjustmentQuantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) ||
                decimal.TryParse(AdjustmentQuantityText, NumberStyles.Number, CultureInfo.CurrentCulture, out qty))
            {
                var projected = SelectedAdjustmentType == ManualStockAdjustmentType.Increase
                    ? AdjustmentCurrentStock + qty
                    : AdjustmentCurrentStock - qty;
                return ProductItemViewModel.FormatQuantity(projected, AdjustmentUnitCode);
            }
            return FormattedCurrentStock;
        }
    }

    // --- Movement Ledger State ---
    [ObservableProperty]
    public partial bool IsMovementsLoading { get; set; }

    [ObservableProperty]
    public partial int MovementsPage { get; set; } = 1;

    [ObservableProperty]
    public partial int MovementsPageSize { get; set; } = 20;

    [ObservableProperty]
    public partial int MovementsTotalCount { get; set; }

    [ObservableProperty]
    public partial int MovementsTotalPages { get; set; } = 1;

    [ObservableProperty]
    public partial Guid? SelectedMovementProductId { get; set; }

    public ObservableCollection<StockMovementViewModel> Movements { get; } = [];

    public InventoryViewModel(InventoryApiClient inventoryClient)
    {
        _inventoryClient = inventoryClient ?? throw new ArgumentNullException(nameof(inventoryClient));
    }

    public async Task LoadStockAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            // Low-stock query provides authoritative low-stock list and TotalCount
            var lowStockRes = await _inventoryClient.GetLowStockProductsAsync(LowStockOnly ? Page : 1, LowStockOnly ? PageSize : 20, ct: ct);
            if (lowStockRes.Success && lowStockRes.Data != null)
            {
                AuthoritativeLowStockCount = lowStockRes.Data.TotalCount;

                if (LowStockOnly)
                {
                    StockItems.Clear();
                    foreach (var item in lowStockRes.Data.Items)
                    {
                        StockItems.Add(item);
                    }
                    TotalCount = lowStockRes.Data.TotalCount;
                    TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
                    IsLoading = false;
                    return;
                }
            }

            // When LowStockOnly is false, load low-stock query items as stock view
            if (!LowStockOnly && lowStockRes.Success && lowStockRes.Data != null)
            {
                StockItems.Clear();
                foreach (var item in lowStockRes.Data.Items)
                {
                    StockItems.Add(item);
                }
                TotalCount = lowStockRes.Data.TotalCount;
                TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unable to load inventory data: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void PrepareAdjustment(StockBalanceDto item)
    {
        ArgumentNullException.ThrowIfNull(item);

        AdjustmentProductId = item.ProductId;
        AdjustmentSku = item.Sku;
        AdjustmentProductName = item.ProductName;
        AdjustmentUnitCode = item.UnitCode;
        AdjustmentCurrentStock = item.CurrentStock;
        SelectedAdjustmentType = ManualStockAdjustmentType.Increase;
        AdjustmentQuantityText = "1";
        AdjustmentReasonText = string.Empty;
        AdjustmentErrorMessage = string.Empty;
        OnPropertyChanged(nameof(FormattedCurrentStock));
        OnPropertyChanged(nameof(FormattedProjectedStock));
    }

    public void UpdateProjectedStock()
    {
        OnPropertyChanged(nameof(FormattedProjectedStock));
    }

    public bool ValidateAdjustment(out string error)
    {
        error = string.Empty;

        if (!decimal.TryParse(AdjustmentQuantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) &&
            !decimal.TryParse(AdjustmentQuantityText, NumberStyles.Number, CultureInfo.CurrentCulture, out qty))
        {
            error = "Adjustment quantity must be a valid positive decimal number.";
            AdjustmentErrorMessage = error;
            return false;
        }

        if (qty <= 0m)
        {
            error = "Adjustment quantity must be greater than zero.";
            AdjustmentErrorMessage = error;
            return false;
        }

        if (!ProductEditViewModel.IsValidQuantityForUnit(qty, AdjustmentUnitCode))
        {
            error = $"Quantity precision invalid for unit {AdjustmentUnitCode}. Whole numbers required for PC, G, and ML; maximum 3 decimals allowed for KG and L.";
            AdjustmentErrorMessage = error;
            return false;
        }

        if (string.IsNullOrWhiteSpace(AdjustmentReasonText))
        {
            error = "Adjustment reason is mandatory.";
            AdjustmentErrorMessage = error;
            return false;
        }

        if (AdjustmentReasonText.Trim().Length > 250)
        {
            error = "Adjustment reason cannot exceed 250 characters.";
            AdjustmentErrorMessage = error;
            return false;
        }

        if (SelectedAdjustmentType == ManualStockAdjustmentType.Decrease)
        {
            if (AdjustmentCurrentStock - qty < 0m)
            {
                error = "Decrease exceeds current stock on hand (Negative stock policy: Disallowed).";
                AdjustmentErrorMessage = error;
                return false;
            }
        }

        AdjustmentErrorMessage = string.Empty;
        return true;
    }

    public async Task<bool> ExecuteAdjustmentAsync(CancellationToken ct = default)
    {
        if (!ValidateAdjustment(out _)) return false;

        if (!decimal.TryParse(AdjustmentQuantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty))
            qty = decimal.Parse(AdjustmentQuantityText, CultureInfo.CurrentCulture);

        var request = new StockAdjustmentRequest(
            AdjustmentProductId,
            SelectedAdjustmentType,
            qty,
            AdjustmentReasonText.Trim());

        IsLoading = true;
        try
        {
            var res = await _inventoryClient.AdjustStockAsync(request, ct: ct);
            if (!res.Success)
            {
                AdjustmentErrorMessage = res.ErrorMessage ?? "Stock adjustment failed on server.";
                return false;
            }

            // Authoritative server re-fetch after mutation
            await LoadStockAsync(ct);
            await LoadMovementsAsync(SelectedMovementProductId, ct);
            return true;
        }
        catch (Exception ex)
        {
            AdjustmentErrorMessage = $"Adjustment error: {ex.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadMovementsAsync(Guid? productId = null, CancellationToken ct = default)
    {
        IsMovementsLoading = true;
        SelectedMovementProductId = productId;

        try
        {
            var res = await _inventoryClient.GetStockMovementsAsync(productId, MovementsPage, MovementsPageSize, ct: ct);
            if (res.Success && res.Data != null)
            {
                Movements.Clear();
                foreach (var item in res.Data.Items)
                {
                    Movements.Add(new StockMovementViewModel(item));
                }
                MovementsTotalCount = res.Data.TotalCount;
                MovementsTotalPages = Math.Max(1, (int)Math.Ceiling((double)MovementsTotalCount / MovementsPageSize));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unable to load movement history: {ex.Message}";
        }
        finally
        {
            IsMovementsLoading = false;
        }
    }
}
