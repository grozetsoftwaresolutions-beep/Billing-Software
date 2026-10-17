using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RetailPOS.Client.Services;
using RetailPOS.Client.ViewModels;
using RetailPOS.Domain.Enums;
using Windows.System;

namespace RetailPOS.Client.Views;

public sealed partial class SalesHistoryPage : Page
{
    public SalesHistoryViewModel ViewModel { get; }

    public bool HasWarningMessage => !string.IsNullOrWhiteSpace(ViewModel.WarningMessage);
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);
    public bool HasReprintMessage => !string.IsNullOrWhiteSpace(ViewModel.ReprintMessage);

    public InfoBarSeverity ReprintBarSeverity => ViewModel.ReprintStatusSeverity switch
    {
        "Success" => InfoBarSeverity.Success,
        "Error" => InfoBarSeverity.Error,
        "Warning" => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational
    };

    public Visibility LoadingVisibility => ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyStateVisibility => ViewModel.IsEmpty && !ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public string PageNumberDisplay => $"Page {ViewModel.CurrentPage} of {ViewModel.TotalPages}";
    public Visibility NoDetailVisibility => ViewModel.SelectedSaleDetail == null && !ViewModel.IsDetailLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailLoadingVisibility => ViewModel.IsDetailLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => ViewModel.SelectedSaleDetail != null && !ViewModel.IsDetailLoading ? Visibility.Visible : Visibility.Collapsed;
    public bool NotReprinting => !ViewModel.IsReprinting;
    public Visibility DuplicateModalVisibility => ViewModel.ShowDuplicateWarningModal ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TaxComponentsVisibility => (ViewModel.SelectedSaleDetail?.TaxComponents?.Count ?? 0) > 0 ? Visibility.Visible : Visibility.Collapsed;

    public SalesHistoryPage()
    {
        var apiClient = new PosApiClient();
        ViewModel = new SalesHistoryViewModel(apiClient);
        this.InitializeComponent();

        ViewModel.PropertyChanged += (_, _) =>
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                this.Bindings.Update();
            });
        };

        this.Loaded += OnPageLoaded;
    }

    public SalesHistoryPage(SalesHistoryViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.InitializeComponent();

        ViewModel.PropertyChanged += (_, _) =>
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                this.Bindings.Update();
            });
        };

        this.Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Programmatic Role Guard: Verify role before executing any queries
        var role = ClientSessionManager.Instance.CurrentRole;
        if (role is RoleType.Cashier or RoleType.Viewer)
        {
            ViewModel.ErrorMessage = "Access Denied: Cashier and Viewer roles are not authorized to view Sales History.";
            this.Bindings.Update();
            return;
        }

        await ViewModel.InitializeAsync();
        this.Bindings.Update();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadSalesAsync();
        this.Bindings.Update();
    }

    private void OnDismissError(InfoBar sender, object args)
    {
        ViewModel.ErrorMessage = string.Empty;
        this.Bindings.Update();
    }

    private void OnDismissReprintMessage(InfoBar sender, object args)
    {
        ViewModel.ReprintMessage = string.Empty;
        this.Bindings.Update();
    }

    private async void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            await ViewModel.SearchAsync();
            this.Bindings.Update();
        }
    }

    private async void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.SearchAsync();
        this.Bindings.Update();
    }

    private async void OnClearClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.ClearFilterAsync();
        this.Bindings.Update();
    }

    private async void OnSalesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SalesListView.SelectedItem is SaleSummaryItemViewModel selectedItem)
        {
            await ViewModel.SelectSaleAsync(selectedItem);
            this.Bindings.Update();
        }
    }

    private async void OnReprintReceiptClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.ReprintReceiptAsync();
        this.Bindings.Update();
    }

    private void OnCancelDuplicateClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelDuplicateRetry();
        this.Bindings.Update();
    }

    private async void OnConfirmDuplicateClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConfirmAndRetryReprintAsync();
        this.Bindings.Update();
    }
}
