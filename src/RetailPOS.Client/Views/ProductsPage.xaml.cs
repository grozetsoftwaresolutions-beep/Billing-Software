using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RetailPOS.Client.ViewModels;
using RetailPOS.Client.Views.Controls;
using RetailPOS.Contracts.Inventory;

namespace RetailPOS.Client.Views;

public sealed partial class ProductsPage : Page
{
    public ProductsViewModel ViewModel { get; }

    public int SelectedTabIndex { get; set; }

    public bool HasSuccessMessage => !string.IsNullOrWhiteSpace(ViewModel.SuccessMessage);
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);
    public Visibility LoadingVisibility => ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CatalogTabVisibility => SelectedTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InventoryTabVisibility => SelectedTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MovementsTabVisibility => SelectedTabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

    public Style? CatalogTabStyle => SelectedTabIndex == 0 ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] : null;
    public Style? InventoryTabStyle => SelectedTabIndex == 1 ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] : null;
    public Style? MovementsTabStyle => SelectedTabIndex == 2 ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] : null;

    public int StatusFilterIndex
    {
        get => ViewModel.ActiveOnlyFilter switch
        {
            true => 1,
            false => 2,
            _ => 0
        };
        set => ViewModel.ActiveOnlyFilter = value switch
        {
            1 => true,
            2 => false,
            _ => null
        };
    }

    public string LowStockFilterButtonText => ViewModel.InventoryVm.AuthoritativeLowStockCount > 0
        ? $"Low Stock Only ({ViewModel.InventoryVm.AuthoritativeLowStockCount})"
        : "Low Stock Only";

    public string PaginationSummaryText => $"Showing Page {ViewModel.Page} of {ViewModel.TotalPages} ({ViewModel.TotalCount} total items)";
    public string PageNumberText => $"{ViewModel.Page} / {ViewModel.TotalPages}";
    public bool CanPagePrevious => ViewModel.Page > 1;
    public bool CanPageNext => ViewModel.Page < ViewModel.TotalPages;

    public ProductsPage()
    {
        ViewModel = new ProductsViewModel();
        this.InitializeComponent();

        Loaded += async (_, _) =>
        {
            await ViewModel.InitializeAsync();
            Bindings.Update();
        };

        ViewModel.PropertyChanged += (_, _) =>
        {
            DispatcherQueue?.TryEnqueue(() => Bindings.Update());
        };
    }

    private void OnTabCatalogClicked(object sender, RoutedEventArgs e)
    {
        SelectedTabIndex = 0;
        Bindings.Update();
    }

    private async void OnTabInventoryClicked(object sender, RoutedEventArgs e)
    {
        SelectedTabIndex = 1;
        Bindings.Update();
        await ViewModel.InventoryVm.LoadStockAsync();
    }

    private async void OnTabMovementsClicked(object sender, RoutedEventArgs e)
    {
        SelectedTabIndex = 2;
        Bindings.Update();
        await ViewModel.InventoryVm.LoadMovementsAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
        Bindings.Update();
    }

    private async void OnNewProductClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.EditViewModel.InitializeForCreate();
        var dialog = new ProductEditDialog(ViewModel.EditViewModel)
        {
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.SaveProductAsync();
            Bindings.Update();
        }
    }

    private async void OnEditProductRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProductItemViewModel item })
        {
            await ViewModel.PrepareProductForEditAsync(item.Dto);
            var dialog = new ProductEditDialog(ViewModel.EditViewModel)
            {
                XamlRoot = this.XamlRoot
            };

            dialog.AddBarcodeRequested += async (_, newBarcode) =>
            {
                await ViewModel.AddBarcodeToProductAsync(item.Id, newBarcode, dialog.ViewModel.NewBarcodeIsPrimary);
                dialog.Hide();
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.SaveProductAsync();
                Bindings.Update();
            }
        }
    }

    private async void OnToggleStatusRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProductItemViewModel item })
        {
            await ViewModel.ToggleProductStatusAsync(item);
            Bindings.Update();
        }
    }

    private async void OnAdjustStockRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StockBalanceDto item })
        {
            ViewModel.InventoryVm.PrepareAdjustment(item);
            var dialog = new StockAdjustmentDialog(ViewModel.InventoryVm)
            {
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var success = await ViewModel.InventoryVm.ExecuteAdjustmentAsync();
                if (success)
                {
                    ViewModel.SuccessMessage = $"Stock adjusted successfully for SKU '{item.Sku}'.";
                }
                Bindings.Update();
            }
        }
    }

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.Page = 1;
        await ViewModel.LoadProductsAsync();
        Bindings.Update();
    }

    private async void OnClearFiltersClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedCategoryId = null;
        ViewModel.SelectedBrandId = null;
        ViewModel.ActiveOnlyFilter = null;
        ViewModel.Page = 1;
        await ViewModel.LoadProductsAsync();
        Bindings.Update();
    }

    private async void OnLowStockToggled(object sender, RoutedEventArgs e)
    {
        await ViewModel.InventoryVm.LoadStockAsync();
        Bindings.Update();
    }

    private async void OnRefreshStockClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.InventoryVm.LoadStockAsync();
        Bindings.Update();
    }

    private async void OnRefreshMovementsClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.InventoryVm.LoadMovementsAsync();
        Bindings.Update();
    }

    private void OnDismissSuccess(InfoBar sender, object args)
    {
        ViewModel.SuccessMessage = string.Empty;
        Bindings.Update();
    }

    private void OnDismissError(InfoBar sender, object args)
    {
        ViewModel.ErrorMessage = string.Empty;
        Bindings.Update();
    }
}
