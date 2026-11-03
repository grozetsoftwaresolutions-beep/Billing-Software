using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RetailPOS.Client.Services;
using RetailPOS.Client.ViewModels;
using RetailPOS.Domain.Enums;
using Windows.System;
using Windows.UI;

namespace RetailPOS.Client.Views;

public sealed partial class PosPage : Page
{
    private static readonly SolidColorBrush ActiveTabBrush = new(Color.FromArgb(255, 37, 99, 235));
    private static readonly SolidColorBrush InactiveTabBrush = new(Color.FromArgb(255, 241, 245, 249));
    private static readonly SolidColorBrush ActiveTextBrush = new(Colors.White);
    private static readonly SolidColorBrush InactiveTextBrush = new(Color.FromArgb(255, 71, 85, 105));

    public PosViewModel ViewModel { get; }

    public Visibility SearchResultsVisibility => ViewModel.HasSearchResults ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => string.IsNullOrEmpty(ViewModel.ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PaymentOverlayVisibility => ViewModel.IsPaymentOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CashFormVisibility => ViewModel.IsCashSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CardFormVisibility => ViewModel.IsCardSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CheckoutSuccessVisibility => ViewModel.IsCheckoutSuccess ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ViewInSalesHistoryVisibility
    {
        get
        {
            if (ViewModel == null) return Visibility.Collapsed;
            var role = ClientSessionManager.Instance.CurrentRole;
            return (role == RoleType.Owner || role == RoleType.Admin || role == RoleType.Manager)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    public SolidColorBrush CashTabBackground => ViewModel.IsCashSelected ? ActiveTabBrush : InactiveTabBrush;
    public SolidColorBrush CashTabForeground => ViewModel.IsCashSelected ? ActiveTextBrush : InactiveTextBrush;
    public SolidColorBrush CardTabBackground => ViewModel.IsCardSelected ? ActiveTabBrush : InactiveTabBrush;
    public SolidColorBrush CardTabForeground => ViewModel.IsCardSelected ? ActiveTextBrush : InactiveTextBrush;

#if DEBUG
    public static string? InitialDemoMode { get; set; }
#endif

    public PosPage()
    {
        ViewModel = new PosViewModel();
        this.InitializeComponent();

        ViewModel.PropertyChanged += (_, _) =>
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                this.Bindings.Update();
            });
        };

        this.Loaded += (_, _) =>
        {
            SearchBox.Focus(FocusState.Programmatic);

#if DEBUG
            if (InitialDemoMode == "cart" || InitialDemoMode == "payment" || InitialDemoMode == "checkout")
            {
                ViewModel.AddProductToCart(new RetailPOS.Contracts.Catalog.ProductPosDto(
                    Guid.NewGuid(), "SKU-SALT", "Tata Salt 1kg", "PCS", 2800, 1, "8901234567890", true, true));
                ViewModel.AddProductToCart(new RetailPOS.Contracts.Catalog.ProductPosDto(
                    Guid.NewGuid(), "SKU-BUTTER", "Amul Butter 500g", "PCS", 27500, 1, "8901234567891", true, true));

                ViewModel.CartItems[0].Quantity = 2m;
                ViewModel.CartItems[0].LineGrossPaise = 5600;
                ViewModel.CartItems[0].LineTotalPaise = 5600;
                ViewModel.GrossSubtotalPaise = 33100;
                ViewModel.TaxableSubtotalPaise = 30000;
                ViewModel.TotalTaxPaise = 3100;
                ViewModel.FinalPayablePaise = 33100;

                if (InitialDemoMode == "payment")
                {
                    ViewModel.OpenPayment();
                    ViewModel.TenderedAmountRupees = "500";
                    ViewModel.UpdateChangeDue();
                }
                else if (InitialDemoMode == "checkout")
                {
                    ViewModel.LastCompletedInvoiceNumber = "INV-20260922-0042";
                    ViewModel.IsCheckoutSuccess = true;
                    ViewModel.CartItems.Clear();
                    ViewModel.GrossSubtotalPaise = 0;
                    ViewModel.FinalPayablePaise = 0;
                }
            }
#endif

            this.Bindings.Update();
        };

        this.KeyDown += OnPageKeyDown;
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.F2:
                SearchBox.Focus(FocusState.Keyboard);
                SearchBox.SelectAll();
                e.Handled = true;
                break;

            case VirtualKey.F4:
                ViewModel.NewBill();
                SearchBox.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;

            case VirtualKey.F12:
                if (ViewModel.IsPaymentOpen)
                {
                    _ = ViewModel.ExecuteCheckoutAsync();
                }
                else if (ViewModel.CanCheckout)
                {
                    ViewModel.OpenPayment();
                }
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                if (ViewModel.IsPaymentOpen)
                {
                    ViewModel.ClosePayment();
                    e.Handled = true;
                }
                else if (ViewModel.HasSearchResults)
                {
                    ViewModel.SearchResults.Clear();
                    ViewModel.HasSearchResults = false;
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            _ = ViewModel.SearchProductsAsync();
            e.Handled = true;
        }
    }

    private void OnSearchResultItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProductSearchResultViewModel item)
        {
            ViewModel.SelectSearchResult(item);
            SearchBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnIncrementQuantityClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CartItemViewModel item)
        {
            ViewModel.IncrementQuantity(item);
        }
    }

    private void OnDecrementQuantityClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CartItemViewModel item)
        {
            ViewModel.DecrementQuantity(item);
        }
    }

    private void OnRemoveItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CartItemViewModel item)
        {
            ViewModel.RemoveItem(item);
        }
    }

    private void OnClosePaymentClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.ClosePayment();
    }

    private void OnSelectCashClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectPaymentMethod("Cash");
        this.Bindings.Update();
    }

    private void OnSelectCardClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectPaymentMethod("Card");
        this.Bindings.Update();
    }

    private void OnPresetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string preset)
        {
            ViewModel.SetPresetCashAmount(preset);
        }
    }

    private void OnDismissSuccessClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.NewBill();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnBackToHomeClicked(object sender, RoutedEventArgs e)
    {
        ShellPage.Current?.NavigateTo("Home");
    }

    private void OnF2SearchClicked(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            _ = ViewModel.SearchProductsAsync();
        }
    }

    private void OnF4NewBillClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.NewBill();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnF12CheckoutClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPaymentOpen)
        {
            _ = ViewModel.ExecuteCheckoutAsync();
        }
        else if (ViewModel.CanCheckout)
        {
            ViewModel.OpenPayment();
        }
    }

    private void OnViewInSalesHistoryClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.NewBill();
        ShellPage.Current?.NavigateTo("SalesHistory");
    }
}
