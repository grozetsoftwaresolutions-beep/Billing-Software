using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetailPOS.Client.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Customers;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed partial class CustomersViewModel : ObservableObject
{
    private static readonly Regex RupeesRegex = new(@"^\d+(\.\d{1,2})?$", RegexOptions.Compiled);
    private readonly PosApiClient _posApiClient;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    // --- Search & Filtering ---
    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ActiveFilterIndex { get; set; } = 0; // 0 = All, 1 = Active, 2 = Inactive

    // --- Pagination ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousPage))]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
    [NotifyPropertyChangedFor(nameof(PaginationSummary))]
    public partial int CurrentPage { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaginationSummary))]
    public partial int PageSize { get; set; } = 20;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
    [NotifyPropertyChangedFor(nameof(PaginationSummary))]
    public partial int TotalCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
    public partial int TotalPages { get; set; } = 1;

    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;

    public string PaginationSummary
    {
        get
        {
            if (TotalCount == 0) return "No customers found";
            var start = (CurrentPage - 1) * PageSize + 1;
            var end = Math.Min(CurrentPage * PageSize, TotalCount);
            return $"Showing {start}–{end} of {TotalCount}";
        }
    }

    // --- Master List ---
    public ObservableCollection<CustomerSummaryItemViewModel> Customers { get; } = [];

    [ObservableProperty]
    public partial CustomerSummaryItemViewModel? SelectedCustomer { get; set; }

    // --- Detail Pane ---
    [ObservableProperty]
    public partial bool IsDetailLoading { get; set; }

    [ObservableProperty]
    public partial string DetailErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedDetailTab { get; set; } = 0; // 0 = Ledger, 1 = Payments, 2 = Sales

    public ObservableCollection<CustomerLedgerItemViewModel> LedgerEntries { get; } = [];
    public ObservableCollection<CustomerPaymentItemViewModel> PaymentHistory { get; } = [];
    public ObservableCollection<CustomerSaleItemViewModel> SalesHistory { get; } = [];

    // --- Role-Based Capabilities ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageCreditLimit))]
    [NotifyPropertyChangedFor(nameof(CanChangeStatus))]
    [NotifyPropertyChangedFor(nameof(CanEditProfile))]
    [NotifyPropertyChangedFor(nameof(CanRecordRepayment))]
    public partial RoleType CurrentRole { get; set; } = RoleType.Cashier;

    public bool CanManageCreditLimit => CurrentRole is RoleType.Owner or RoleType.Admin or RoleType.Manager;
    public bool CanChangeStatus => CurrentRole is RoleType.Owner or RoleType.Admin or RoleType.Manager;
    public bool CanEditProfile => CurrentRole != RoleType.Viewer;
    public bool CanRecordRepayment => CurrentRole != RoleType.Viewer;

    // --- Customer Create / Edit Form State ---
    [ObservableProperty]
    public partial bool IsCreateOrEditOpen { get; set; }

    [ObservableProperty]
    public partial bool IsEditingExisting { get; set; }

    public string CreateOrEditTitle => IsEditingExisting ? "Edit Customer Profile" : "Add New Customer";

    [ObservableProperty]
    public partial string FormName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FormPhone { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FormEmail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FormAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FormGstin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FormCreditLimitRupees { get; set; } = "0";

    [ObservableProperty]
    public partial string FormNotes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FormValidationError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFormSubmitting { get; set; }

    // --- Adjust Credit Limit Form State ---
    [ObservableProperty]
    public partial bool IsCreditLimitDialogOpen { get; set; }

    [ObservableProperty]
    public partial string NewCreditLimitRupees { get; set; } = "0";

    [ObservableProperty]
    public partial string CreditLimitReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CreditLimitValidationError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCreditLimitSubmitting { get; set; }

    // --- Set Active Status Form State ---
    [ObservableProperty]
    public partial bool IsStatusDialogOpen { get; set; }

    [ObservableProperty]
    public partial bool TargetStatus { get; set; }

    public string StatusDialogTitle => TargetStatus ? "Activate Customer" : "Deactivate Customer";

    [ObservableProperty]
    public partial string StatusReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusValidationError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStatusSubmitting { get; set; }

    // --- Repayment Workflow State (Safety Rules #1 & #2) ---
    [ObservableProperty]
    public partial bool IsRepaymentDialogOpen { get; set; }

    [ObservableProperty]
    public partial string RepaymentAmountRupees { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PaymentMethod RepaymentMethod { get; set; } = PaymentMethod.Cash;

    [ObservableProperty]
    public partial string RepaymentReferenceNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepaymentNotes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepaymentValidationError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepaymentStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepaymentStatusSeverity { get; set; } = "Info"; // Info, Success, Warning, Error

    [ObservableProperty]
    public partial bool IsRepaymentSubmitting { get; set; }

    [ObservableProperty]
    public partial bool IsRepaymentAmbiguous { get; set; }

    /// <summary>
    /// Preserved ClientTransactionId for retries of the same logical repayment operation.
    /// Safety Invariant #1: Never replace this ID merely because the first request timed out or returned ambiguous status.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetrySameRepayment))]
    public partial Guid? PendingRepaymentTxId { get; set; }

    public bool CanRetrySameRepayment => IsRepaymentAmbiguous && PendingRepaymentTxId.HasValue && PendingRepaymentTxId.Value != Guid.Empty;

    public CustomersViewModel(PosApiClient? posApiClient = null)
    {
        _posApiClient = posApiClient ?? new PosApiClient();
        CurrentRole = ClientSessionManager.Instance.CurrentRole ?? RoleType.Cashier;
    }

    // --- Helper: Exact Rupees to Paise Conversion (Safety Rule #2) ---
    public static bool TryParseRupeesToPaise(string? input, out long paise, out string errorMessage, bool allowZero = false)
    {
        paise = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = "Amount is required.";
            return false;
        }

        var trimmed = input.Trim();
        if (!RupeesRegex.IsMatch(trimmed))
        {
            errorMessage = "Please enter a valid amount in rupees (up to 2 decimal places, e.g. 150 or 150.50).";
            return false;
        }

        if (!decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var decimalRupees))
        {
            errorMessage = "Unable to parse monetary amount.";
            return false;
        }

        if (allowZero)
        {
            if (decimalRupees < 0)
            {
                errorMessage = "Amount cannot be negative.";
                return false;
            }
        }
        else
        {
            if (decimalRupees <= 0)
            {
                errorMessage = "Payment amount must be greater than zero.";
                return false;
            }
        }

        var calculatedPaise = decimalRupees * 100m;
        if (calculatedPaise > long.MaxValue || calculatedPaise != decimal.Truncate(calculatedPaise))
        {
            errorMessage = "Monetary amount contains invalid fractional precision.";
            return false;
        }

        paise = (long)calculatedPaise;
        errorMessage = string.Empty;
        return true;
    }

    // --- Navigation / Lifecycle ---
    public async Task InitializeAsync()
    {
        CurrentPage = 1;
        await SearchCustomersAsync();
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        CurrentPage = 1;
        await SearchCustomersAsync();
    }

    [RelayCommand]
    public async Task ClearSearchAsync()
    {
        SearchQuery = string.Empty;
        ActiveFilterIndex = 0;
        CurrentPage = 1;
        await SearchCustomersAsync();
    }

    [RelayCommand]
    public async Task NextPageAsync()
    {
        if (HasNextPage)
        {
            CurrentPage++;
            await SearchCustomersAsync();
        }
    }

    [RelayCommand]
    public async Task PreviousPageAsync()
    {
        if (HasPreviousPage)
        {
            CurrentPage--;
            await SearchCustomersAsync();
        }
    }

    [RelayCommand]
    public async Task RefreshListAsync()
    {
        await SearchCustomersAsync();
    }

    private async Task SearchCustomersAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;

        bool? isActiveFilter = ActiveFilterIndex switch
        {
            1 => true,
            2 => false,
            _ => null
        };

        try
        {
            var response = await _posApiClient.SearchCustomersAsync(
                SearchQuery,
                isActiveFilter,
                CurrentPage,
                PageSize);

            if (response.Success && response.Data != null)
            {
                TotalCount = response.Data.TotalCount;
                TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
                Customers.Clear();

                foreach (var dto in response.Data.Items)
                {
                    Customers.Add(new CustomerSummaryItemViewModel(dto));
                }

                IsEmpty = Customers.Count == 0;

                // Maintain selection or select first
                if (SelectedCustomer != null)
                {
                    var existing = Customers.FirstOrDefault(c => c.Id == SelectedCustomer.Id);
                    SelectedCustomer = existing;
                }
            }
            else
            {
                ErrorMessage = response.ErrorMessage ?? "Failed to load customers.";
                Customers.Clear();
                IsEmpty = true;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unable to connect to store server: {ex.Message}";
            Customers.Clear();
            IsEmpty = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // --- Master-Detail Selection ---
    async partial void OnSelectedCustomerChanged(CustomerSummaryItemViewModel? value)
    {
        if (value != null)
        {
            await LoadCustomerDetailsAsync(value.Id);
        }
        else
        {
            LedgerEntries.Clear();
            PaymentHistory.Clear();
            SalesHistory.Clear();
        }
    }

    [RelayCommand]
    public async Task RefreshCustomerDetailsAsync()
    {
        if (SelectedCustomer != null)
        {
            await LoadCustomerDetailsAsync(SelectedCustomer.Id);
        }
    }

    private async Task LoadCustomerDetailsAsync(Guid customerId)
    {
        IsDetailLoading = true;
        DetailErrorMessage = string.Empty;

        try
        {
            // 1. Refresh customer master dto to guarantee authoritative balances
            var custResponse = await _posApiClient.GetCustomerByIdAsync(customerId);
            if (custResponse.Success && custResponse.Data != null)
            {
                SelectedCustomer?.Update(custResponse.Data);
            }

            // 2. Load Ledger
            var ledgerResponse = await _posApiClient.GetCustomerLedgerAsync(customerId);
            LedgerEntries.Clear();
            if (ledgerResponse.Success && ledgerResponse.Data != null)
            {
                foreach (var entry in ledgerResponse.Data)
                {
                    LedgerEntries.Add(new CustomerLedgerItemViewModel(entry));
                }
            }

            // 3. Load Payments
            var paymentsResponse = await _posApiClient.GetCustomerPaymentsAsync(customerId);
            PaymentHistory.Clear();
            if (paymentsResponse.Success && paymentsResponse.Data != null)
            {
                foreach (var payment in paymentsResponse.Data)
                {
                    PaymentHistory.Add(new CustomerPaymentItemViewModel(payment));
                }
            }

            // 4. Load Sales
            var salesResponse = await _posApiClient.GetCustomerSalesAsync(customerId);
            SalesHistory.Clear();
            if (salesResponse.Success && salesResponse.Data != null)
            {
                foreach (var sale in salesResponse.Data)
                {
                    SalesHistory.Add(new CustomerSaleItemViewModel(sale));
                }
            }
        }
        catch (Exception ex)
        {
            DetailErrorMessage = $"Failed to load customer details: {ex.Message}";
        }
        finally
        {
            IsDetailLoading = false;
        }
    }

    // --- Create / Edit Customer Profile Workflow ---
    [RelayCommand]
    public void OpenCreateCustomerDialog()
    {
        IsEditingExisting = false;
        FormName = string.Empty;
        FormPhone = string.Empty;
        FormEmail = string.Empty;
        FormAddress = string.Empty;
        FormGstin = string.Empty;
        FormCreditLimitRupees = "0";
        FormNotes = string.Empty;
        FormValidationError = string.Empty;
        IsCreateOrEditOpen = true;
    }

    [RelayCommand]
    public void OpenEditCustomerDialog()
    {
        if (SelectedCustomer == null) return;

        IsEditingExisting = true;
        FormName = SelectedCustomer.Name;
        FormPhone = SelectedCustomer.Phone ?? string.Empty;
        FormEmail = SelectedCustomer.Email ?? string.Empty;
        FormAddress = SelectedCustomer.Address ?? string.Empty;
        FormGstin = SelectedCustomer.Gstin ?? string.Empty;
        FormCreditLimitRupees = (SelectedCustomer.CreditLimitPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture);
        FormNotes = SelectedCustomer.Notes ?? string.Empty;
        FormValidationError = string.Empty;
        IsCreateOrEditOpen = true;
    }

    [RelayCommand]
    public void CancelCreateOrEdit()
    {
        IsCreateOrEditOpen = false;
        FormValidationError = string.Empty;
    }

    [RelayCommand]
    public async Task SaveCustomerProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(FormName))
        {
            FormValidationError = "Customer name is mandatory.";
            return;
        }

        long creditLimitPaise = 0;
        if (!IsEditingExisting)
        {
            if (!TryParseRupeesToPaise(FormCreditLimitRupees, out creditLimitPaise, out var limitError, allowZero: true))
            {
                FormValidationError = limitError;
                return;
            }
        }

        IsFormSubmitting = true;
        FormValidationError = string.Empty;

        try
        {
            if (IsEditingExisting)
            {
                if (SelectedCustomer == null) return;

                var updateRequest = new UpdateCustomerRequest(
                    FormName.Trim(),
                    string.IsNullOrWhiteSpace(FormPhone) ? null : FormPhone.Trim(),
                    string.IsNullOrWhiteSpace(FormEmail) ? null : FormEmail.Trim(),
                    string.IsNullOrWhiteSpace(FormAddress) ? null : FormAddress.Trim(),
                    string.IsNullOrWhiteSpace(FormGstin) ? null : FormGstin.Trim(),
                    string.IsNullOrWhiteSpace(FormNotes) ? null : FormNotes.Trim());

                var response = await _posApiClient.UpdateCustomerAsync(SelectedCustomer.Id, updateRequest);
                if (response.Success && response.Data != null)
                {
                    SelectedCustomer.Update(response.Data);
                    IsCreateOrEditOpen = false;
                    await RefreshListAsync();
                }
                else
                {
                    FormValidationError = response.ErrorMessage ?? "Failed to update customer.";
                }
            }
            else
            {
                var createRequest = new CreateCustomerRequest(
                    FormName.Trim(),
                    string.IsNullOrWhiteSpace(FormPhone) ? null : FormPhone.Trim(),
                    string.IsNullOrWhiteSpace(FormEmail) ? null : FormEmail.Trim(),
                    string.IsNullOrWhiteSpace(FormAddress) ? null : FormAddress.Trim(),
                    string.IsNullOrWhiteSpace(FormGstin) ? null : FormGstin.Trim(),
                    creditLimitPaise,
                    string.IsNullOrWhiteSpace(FormNotes) ? null : FormNotes.Trim());

                var response = await _posApiClient.CreateCustomerAsync(createRequest);
                if (response.Success && response.Data != null)
                {
                    IsCreateOrEditOpen = false;
                    await SearchCustomersAsync();
                    var createdItem = Customers.FirstOrDefault(c => c.Id == response.Data.Id);
                    if (createdItem != null)
                    {
                        SelectedCustomer = createdItem;
                    }
                }
                else
                {
                    FormValidationError = response.ErrorMessage ?? "Failed to create customer.";
                }
            }
        }
        catch (Exception ex)
        {
            FormValidationError = $"Error saving customer: {ex.Message}";
        }
        finally
        {
            IsFormSubmitting = false;
        }
    }

    // --- Adjust Credit Limit Workflow (Role-restricted) ---
    [RelayCommand]
    public void OpenCreditLimitDialog()
    {
        if (SelectedCustomer == null || !CanManageCreditLimit) return;

        NewCreditLimitRupees = (SelectedCustomer.CreditLimitPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture);
        CreditLimitReason = string.Empty;
        CreditLimitValidationError = string.Empty;
        IsCreditLimitDialogOpen = true;
    }

    [RelayCommand]
    public void CancelCreditLimitDialog()
    {
        IsCreditLimitDialogOpen = false;
        CreditLimitValidationError = string.Empty;
    }

    [RelayCommand]
    public async Task SaveCreditLimitAsync()
    {
        if (SelectedCustomer == null || !CanManageCreditLimit) return;

        if (!TryParseRupeesToPaise(NewCreditLimitRupees, out var newLimitPaise, out var limitError, allowZero: true))
        {
            CreditLimitValidationError = limitError;
            return;
        }

        if (string.IsNullOrWhiteSpace(CreditLimitReason))
        {
            CreditLimitValidationError = "Reason is mandatory when modifying credit limit.";
            return;
        }

        IsCreditLimitSubmitting = true;
        CreditLimitValidationError = string.Empty;

        try
        {
            var request = new UpdateCreditLimitRequest(newLimitPaise, CreditLimitReason.Trim());
            var response = await _posApiClient.UpdateCreditLimitAsync(SelectedCustomer.Id, request);
            if (response.Success && response.Data != null)
            {
                SelectedCustomer.Update(response.Data);
                IsCreditLimitDialogOpen = false;
                await RefreshCustomerDetailsAsync();
            }
            else
            {
                CreditLimitValidationError = response.ErrorMessage ?? "Failed to update credit limit.";
            }
        }
        catch (Exception ex)
        {
            CreditLimitValidationError = $"Error updating credit limit: {ex.Message}";
        }
        finally
        {
            IsCreditLimitSubmitting = false;
        }
    }

    // --- Set Active Status Workflow (Role-restricted) ---
    [RelayCommand]
    public void OpenStatusDialog(bool targetActive)
    {
        if (SelectedCustomer == null || !CanChangeStatus) return;

        TargetStatus = targetActive;
        StatusReason = string.Empty;
        StatusValidationError = string.Empty;
        IsStatusDialogOpen = true;
    }

    [RelayCommand]
    public void CancelStatusDialog()
    {
        IsStatusDialogOpen = false;
        StatusValidationError = string.Empty;
    }

    [RelayCommand]
    public async Task SaveStatusAsync()
    {
        if (SelectedCustomer == null || !CanChangeStatus) return;

        if (string.IsNullOrWhiteSpace(StatusReason))
        {
            StatusValidationError = "Reason is mandatory when changing customer status.";
            return;
        }

        IsStatusSubmitting = true;
        StatusValidationError = string.Empty;

        try
        {
            var request = new SetCustomerStatusRequest(TargetStatus, StatusReason.Trim());
            var response = await _posApiClient.SetCustomerStatusAsync(SelectedCustomer.Id, request);
            if (response.Success && response.Data != null)
            {
                SelectedCustomer.Update(response.Data);
                IsStatusDialogOpen = false;
                await RefreshListAsync();
            }
            else
            {
                StatusValidationError = response.ErrorMessage ?? "Failed to change customer status.";
            }
        }
        catch (Exception ex)
        {
            StatusValidationError = $"Error updating status: {ex.Message}";
        }
        finally
        {
            IsStatusSubmitting = false;
        }
    }

    // --- Repayment Workflow (Safety Invariants #1 & #2) ---
    [RelayCommand]
    public void OpenRepaymentDialog()
    {
        if (SelectedCustomer == null || !CanRecordRepayment) return;

        // Operator explicitly starts a new repayment: generate a new unique ClientTransactionId
        PendingRepaymentTxId = Guid.NewGuid();
        RepaymentAmountRupees = string.Empty;
        RepaymentMethod = PaymentMethod.Cash;
        RepaymentReferenceNumber = string.Empty;
        RepaymentNotes = string.Empty;
        RepaymentValidationError = string.Empty;
        RepaymentStatusMessage = string.Empty;
        RepaymentStatusSeverity = "Info";
        IsRepaymentAmbiguous = false;
        IsRepaymentDialogOpen = true;
    }

    [RelayCommand]
    public void CloseRepaymentDialog()
    {
        IsRepaymentDialogOpen = false;
        RepaymentValidationError = string.Empty;
        RepaymentStatusMessage = string.Empty;
    }

    [RelayCommand]
    public async Task SubmitRepaymentAsync()
    {
        if (SelectedCustomer == null || !CanRecordRepayment) return;

        // Safety Invariant #2: Exact rupees-to-paise conversion. Reject invalid input with ZERO HTTP POSTs.
        if (!TryParseRupeesToPaise(RepaymentAmountRupees, out var amountPaise, out var parseError, allowZero: false))
        {
            RepaymentValidationError = parseError;
            return;
        }

        if (RepaymentMethod != PaymentMethod.Cash && string.IsNullOrWhiteSpace(RepaymentReferenceNumber))
        {
            RepaymentValidationError = $"Reference number is mandatory for {RepaymentMethod} payments.";
            return;
        }

        // Safety Invariant #1: Reuse PendingRepaymentTxId for this logical repayment.
        if (!PendingRepaymentTxId.HasValue || PendingRepaymentTxId.Value == Guid.Empty)
        {
            PendingRepaymentTxId = Guid.NewGuid();
        }

        var clientTxId = PendingRepaymentTxId.Value;

        IsRepaymentSubmitting = true;
        RepaymentValidationError = string.Empty;
        RepaymentStatusMessage = "Recording repayment on store server...";
        RepaymentStatusSeverity = "Info";

        try
        {
            var request = new RecordCustomerPaymentRequest(
                amountPaise,
                RepaymentMethod,
                string.IsNullOrWhiteSpace(RepaymentReferenceNumber) ? null : RepaymentReferenceNumber.Trim(),
                string.IsNullOrWhiteSpace(RepaymentNotes) ? null : RepaymentNotes.Trim(),
                clientTxId);

            var response = await _posApiClient.RecordCustomerPaymentAsync(SelectedCustomer.Id, request);
            if (response.Success)
            {
                IsRepaymentAmbiguous = false;
                PendingRepaymentTxId = null;
                RepaymentStatusMessage = $"Repayment of {CustomerSummaryItemViewModel.FormatCurrency(amountPaise)} successfully recorded.";
                RepaymentStatusSeverity = "Success";

                await RefreshCustomerDetailsAsync();
                await RefreshListAsync();
                IsRepaymentDialogOpen = false;
            }
            else
            {
                // Explicit rejection from server
                RepaymentStatusMessage = response.ErrorMessage ?? "Repayment recording failed.";
                RepaymentStatusSeverity = "Error";
                // Retain PendingRepaymentTxId for safety in case of timeout or duplicate call
                IsRepaymentAmbiguous = true;
            }
        }
        catch (Exception ex)
        {
            // Network or connection failure: outcome is uncertain / ambiguous!
            // Safety Invariant #1: Retain PendingRepaymentTxId! Do NOT generate a replacement ID!
            RepaymentStatusMessage = $"Connection or timeout error: {ex.Message}. The server may have processed this payment. Verify customer status or retry using the same transaction ID.";
            RepaymentStatusSeverity = "Warning";
            IsRepaymentAmbiguous = true;
        }
        finally
        {
            IsRepaymentSubmitting = false;
        }
    }

    [RelayCommand]
    public async Task RetryRepaymentAsync()
    {
        // Safety Invariant #1: Retry with the exact same PendingRepaymentTxId
        if (!CanRetrySameRepayment) return;
        await SubmitRepaymentAsync();
    }

    [RelayCommand]
    public async Task VerifyRepaymentAsync()
    {
        if (SelectedCustomer == null || !PendingRepaymentTxId.HasValue) return;

        IsRepaymentSubmitting = true;
        RepaymentStatusMessage = "Verifying transaction status with store server...";
        RepaymentStatusSeverity = "Info";

        try
        {
            var response = await _posApiClient.GetCustomerPaymentsAsync(SelectedCustomer.Id);
            if (response.Success && response.Data != null)
            {
                var match = response.Data.FirstOrDefault(p => p.ClientTransactionId == PendingRepaymentTxId.Value);
                if (match != null)
                {
                    RepaymentStatusMessage = $"Payment confirmed! Successfully found on server: {CustomerSummaryItemViewModel.FormatCurrency(match.AmountPaise)} via {match.Method}.";
                    RepaymentStatusSeverity = "Success";
                    IsRepaymentAmbiguous = false;
                    PendingRepaymentTxId = null;
                    await RefreshCustomerDetailsAsync();
                    await RefreshListAsync();
                    return;
                }
            }

            RepaymentStatusMessage = "Payment not found on server for this transaction ID. You may safely retry.";
            RepaymentStatusSeverity = "Info";
        }
        catch (Exception ex)
        {
            RepaymentStatusMessage = $"Verification failed: {ex.Message}";
            RepaymentStatusSeverity = "Error";
        }
        finally
        {
            IsRepaymentSubmitting = false;
        }
    }
}
