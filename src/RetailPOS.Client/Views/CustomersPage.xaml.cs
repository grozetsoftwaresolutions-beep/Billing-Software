using System;
using Microsoft.UI.Text;
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

public sealed partial class CustomersPage : Page
{
    private static readonly SolidColorBrush ActiveTabBrush = new(Color.FromArgb(255, 30, 62, 98));
    private static readonly SolidColorBrush InactiveTabBrush = new(Color.FromArgb(255, 241, 245, 249));
    private static readonly SolidColorBrush ActiveTextBrush = new(Color.FromArgb(255, 255, 255, 255));
    private static readonly SolidColorBrush InactiveTextBrush = new(Color.FromArgb(255, 51, 65, 85));

    public CustomersViewModel ViewModel { get; }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);
    public Visibility LoadingVisibility => ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyStateVisibility => ViewModel.IsEmpty && !ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CanCreateCustomerVisibility => ViewModel.CanEditProfile ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoCustomerSelectedVisibility => ViewModel.SelectedCustomer == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CustomerSelectedVisibility => ViewModel.SelectedCustomer != null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CanRepayVisibility => ViewModel.CanRecordRepayment ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CanEditVisibility => ViewModel.CanEditProfile ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CanManageCreditLimitVisibility => ViewModel.CanManageCreditLimit ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CanChangeStatusVisibility => ViewModel.CanChangeStatus ? Visibility.Visible : Visibility.Collapsed;

    public string ToggleStatusButtonText => (ViewModel.SelectedCustomer?.IsActive ?? false) ? "Deactivate" : "Activate";

    public string PhoneDisplay => string.IsNullOrWhiteSpace(ViewModel.SelectedCustomer?.Phone) ? "Phone: —" : $"Phone: {ViewModel.SelectedCustomer.Phone}";
    public string EmailDisplay => string.IsNullOrWhiteSpace(ViewModel.SelectedCustomer?.Email) ? "Email: —" : $"Email: {ViewModel.SelectedCustomer.Email}";
    public string GstinDisplay => string.IsNullOrWhiteSpace(ViewModel.SelectedCustomer?.Gstin) ? "GSTIN: —" : $"GSTIN: {ViewModel.SelectedCustomer.Gstin}";
    public string AddressDisplay => string.IsNullOrWhiteSpace(ViewModel.SelectedCustomer?.Address) ? "Address: —" : $"Address: {ViewModel.SelectedCustomer.Address}";

    // Tab Bar
    public Visibility LedgerTabVisibility => ViewModel.SelectedDetailTab == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PaymentsTabVisibility => ViewModel.SelectedDetailTab == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SalesTabVisibility => ViewModel.SelectedDetailTab == 2 ? Visibility.Visible : Visibility.Collapsed;

    public FontWeight LedgerTabWeight => ViewModel.SelectedDetailTab == 0 ? FontWeights.Bold : FontWeights.Normal;
    public SolidColorBrush LedgerTabBackground => ViewModel.SelectedDetailTab == 0 ? ActiveTabBrush : InactiveTabBrush;
    public SolidColorBrush LedgerTabForeground => ViewModel.SelectedDetailTab == 0 ? ActiveTextBrush : InactiveTextBrush;

    public FontWeight PaymentsTabWeight => ViewModel.SelectedDetailTab == 1 ? FontWeights.Bold : FontWeights.Normal;
    public SolidColorBrush PaymentsTabBackground => ViewModel.SelectedDetailTab == 1 ? ActiveTabBrush : InactiveTabBrush;
    public SolidColorBrush PaymentsTabForeground => ViewModel.SelectedDetailTab == 1 ? ActiveTextBrush : InactiveTextBrush;

    public FontWeight SalesTabWeight => ViewModel.SelectedDetailTab == 2 ? FontWeights.Bold : FontWeights.Normal;
    public SolidColorBrush SalesTabBackground => ViewModel.SelectedDetailTab == 2 ? ActiveTabBrush : InactiveTabBrush;
    public SolidColorBrush SalesTabForeground => ViewModel.SelectedDetailTab == 2 ? ActiveTextBrush : InactiveTextBrush;

    // Modals
    public Visibility CreateOrEditModalVisibility => ViewModel.IsCreateOrEditOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CreateCreditLimitVisibility => !ViewModel.IsEditingExisting ? Visibility.Visible : Visibility.Collapsed;
    public bool HasFormValidationError => !string.IsNullOrWhiteSpace(ViewModel.FormValidationError);

    public Visibility RepaymentModalVisibility => ViewModel.IsRepaymentDialogOpen ? Visibility.Visible : Visibility.Collapsed;
    public bool HasRepaymentStatus => !string.IsNullOrWhiteSpace(ViewModel.RepaymentStatusMessage);
    public bool HasRepaymentValidationError => !string.IsNullOrWhiteSpace(ViewModel.RepaymentValidationError);

    public InfoBarSeverity RepaymentBarSeverity => ViewModel.RepaymentStatusSeverity switch
    {
        "Success" => InfoBarSeverity.Success,
        "Error" => InfoBarSeverity.Error,
        "Warning" => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational
    };

    public Visibility AmbiguousButtonsVisibility => ViewModel.IsRepaymentAmbiguous ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NormalSubmitButtonVisibility => !ViewModel.IsRepaymentAmbiguous ? Visibility.Visible : Visibility.Collapsed;

    public int RepaymentMethodIndex
    {
        get => ViewModel.RepaymentMethod switch
        {
            PaymentMethod.Cash => 0,
            PaymentMethod.Card => 1,
            PaymentMethod.UPI => 2,
            PaymentMethod.Cheque => 3,
            _ => 0
        };
        set => ViewModel.RepaymentMethod = value switch
        {
            1 => PaymentMethod.Card,
            2 => PaymentMethod.UPI,
            3 => PaymentMethod.Cheque,
            _ => PaymentMethod.Cash
        };
    }

    public Visibility CreditLimitModalVisibility => ViewModel.IsCreditLimitDialogOpen ? Visibility.Visible : Visibility.Collapsed;
    public bool HasCreditLimitValidationError => !string.IsNullOrWhiteSpace(ViewModel.CreditLimitValidationError);

    public Visibility StatusModalVisibility => ViewModel.IsStatusDialogOpen ? Visibility.Visible : Visibility.Collapsed;
    public bool HasStatusValidationError => !string.IsNullOrWhiteSpace(ViewModel.StatusValidationError);

    public CustomersPage()
    {
        var apiClient = new PosApiClient();
        ViewModel = new CustomersViewModel(apiClient);
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

    public CustomersPage(CustomersViewModel viewModel)
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
        var role = ClientSessionManager.Instance.CurrentRole;
        if (role is RoleType.Viewer)
        {
            ViewModel.ErrorMessage = "Access Denied: Viewer role is not authorized to access Customers & Credit.";
            this.Bindings.Update();
            return;
        }

        await ViewModel.InitializeAsync();
        this.Bindings.Update();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshListAsync();
        this.Bindings.Update();
    }

    private void OnDismissError(InfoBar sender, object args)
    {
        ViewModel.ErrorMessage = string.Empty;
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

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        await ViewModel.SearchAsync();
        this.Bindings.Update();
    }

    private async void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.SearchAsync();
        this.Bindings.Update();
    }

    private async void OnPrevPageClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.PreviousPageAsync();
        this.Bindings.Update();
    }

    private async void OnNextPageClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.NextPageAsync();
        this.Bindings.Update();
    }

    private void OnSelectLedgerTab(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedDetailTab = 0;
        this.Bindings.Update();
    }

    private void OnSelectPaymentsTab(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedDetailTab = 1;
        this.Bindings.Update();
    }

    private void OnSelectSalesTab(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedDetailTab = 2;
        this.Bindings.Update();
    }

    private void OnNewCustomerClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenCreateCustomerDialog();
        this.Bindings.Update();
    }

    private void OnEditProfileClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenEditCustomerDialog();
        this.Bindings.Update();
    }

    private void OnCancelCreateOrEdit(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelCreateOrEdit();
        this.Bindings.Update();
    }

    private async void OnSaveCustomer(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveCustomerProfileAsync();
        this.Bindings.Update();
    }

    private void OnAdjustCreditLimitClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenCreditLimitDialog();
        this.Bindings.Update();
    }

    private void OnCancelCreditLimit(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelCreditLimitDialog();
        this.Bindings.Update();
    }

    private async void OnSaveCreditLimit(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveCreditLimitAsync();
        this.Bindings.Update();
    }

    private void OnToggleStatusClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCustomer == null) return;
        ViewModel.OpenStatusDialog(!ViewModel.SelectedCustomer.IsActive);
        this.Bindings.Update();
    }

    private void OnCancelStatus(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelStatusDialog();
        this.Bindings.Update();
    }

    private async void OnSaveStatus(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveStatusAsync();
        this.Bindings.Update();
    }

    private void OnRecordRepaymentClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenRepaymentDialog();
        this.Bindings.Update();
    }

    private void OnCloseRepayment(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseRepaymentDialog();
        this.Bindings.Update();
    }

    private async void OnSubmitRepayment(object sender, RoutedEventArgs e)
    {
        await ViewModel.SubmitRepaymentAsync();
        this.Bindings.Update();
    }

    private async void OnRetryRepayment(object sender, RoutedEventArgs e)
    {
        await ViewModel.RetryRepaymentAsync();
        this.Bindings.Update();
    }

    private async void OnVerifyRepayment(object sender, RoutedEventArgs e)
    {
        await ViewModel.VerifyRepaymentAsync();
        this.Bindings.Update();
    }

    private async void OnRefreshAccountClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshCustomerDetailsAsync();
        this.Bindings.Update();
    }
}
