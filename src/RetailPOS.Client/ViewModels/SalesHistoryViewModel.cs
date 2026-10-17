using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetailPOS.Client.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed partial class SalesHistoryViewModel : ObservableObject
{
    private readonly PosApiClient _posApiClient;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WarningMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDateFilterEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string SearchInvoiceNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? StartDate { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? EndDate { get; set; }

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
            if (TotalCount == 0) return "No sales found";
            var start = (CurrentPage - 1) * PageSize + 1;
            var end = Math.Min(CurrentPage * PageSize, TotalCount);
            return $"Showing {start}–{end} of {TotalCount}";
        }
    }

    public ObservableCollection<SaleSummaryItemViewModel> Sales { get; } = [];

    [ObservableProperty]
    public partial SaleSummaryItemViewModel? SelectedSale { get; set; }

    [ObservableProperty]
    public partial SaleInvoiceDetailViewModel? SelectedSaleDetail { get; set; }

    [ObservableProperty]
    public partial bool IsDetailLoading { get; set; }

    [ObservableProperty]
    public partial string DetailErrorMessage { get; set; } = string.Empty;

    // --- Reprint State Machine ---
    [ObservableProperty]
    public partial bool IsReprinting { get; set; }

    [ObservableProperty]
    public partial string ReprintMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReprintStatusSeverity { get; set; } = "Info"; // Info, Success, Warning, Error

    [ObservableProperty]
    public partial bool ShowDuplicateWarningModal { get; set; }

    [ObservableProperty]
    public partial string DuplicateWarningMessage { get; set; } = string.Empty;

    public Guid? PendingRetryInvoiceId { get; private set; }

    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;
    private bool _isTimeZoneInitialized;

    public TimeZoneInfo StoreTimeZone => _timeZone;

    public SalesHistoryViewModel(PosApiClient posApiClient)
    {
        _posApiClient = posApiClient ?? throw new ArgumentNullException(nameof(posApiClient));
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_isTimeZoneInitialized) return;

        // Verify role before initializing
        if (!IsAuthorizedRole())
        {
            ErrorMessage = "Unauthorized: Access to Sales History is restricted to Owner, Admin, and Manager.";
            return;
        }

        try
        {
            var settingsResult = await _posApiClient.GetStoreSettingsAsync(ct: ct);
            if (settingsResult.Success && settingsResult.Data != null && !string.IsNullOrWhiteSpace(settingsResult.Data.TimeZoneId))
            {
                if (TryResolveTimeZone(settingsResult.Data.TimeZoneId, out var resolvedTz))
                {
                    _timeZone = resolvedTz;
                    IsDateFilterEnabled = true;
                }
                else
                {
                    _timeZone = TimeZoneInfo.Utc;
                    IsDateFilterEnabled = false;
                    WarningMessage = $"Store time zone '{settingsResult.Data.TimeZoneId}' could not be resolved on this system. Date range filtering is disabled. Invoice number search remains available.";
                }
            }
            else
            {
                // Fallback to default India Standard Time
                if (TryResolveTimeZone("India Standard Time", out var defaultTz))
                {
                    _timeZone = defaultTz;
                }
                WarningMessage = "Could not retrieve store settings from server. Defaulting to India Standard Time.";
                IsDateFilterEnabled = true;
            }
        }
        catch
        {
            if (TryResolveTimeZone("India Standard Time", out var defaultTz))
            {
                _timeZone = defaultTz;
            }
            WarningMessage = "Store server communication failed for store settings. Defaulting to India Standard Time.";
            IsDateFilterEnabled = true;
        }
        finally
        {
            _isTimeZoneInitialized = true;
        }

        await LoadSalesAsync(ct);
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct = default)
    {
        CurrentPage = 1;
        await LoadSalesAsync(ct);
    }

    [RelayCommand]
    public async Task ClearFilterAsync(CancellationToken ct = default)
    {
        SearchInvoiceNumber = string.Empty;
        StartDate = null;
        EndDate = null;
        CurrentPage = 1;
        await LoadSalesAsync(ct);
    }

    [RelayCommand]
    public async Task NextPageAsync(CancellationToken ct = default)
    {
        if (HasNextPage)
        {
            CurrentPage++;
            await LoadSalesAsync(ct);
        }
    }

    [RelayCommand]
    public async Task PreviousPageAsync(CancellationToken ct = default)
    {
        if (HasPreviousPage)
        {
            CurrentPage--;
            await LoadSalesAsync(ct);
        }
    }

    public async Task LoadSalesAsync(CancellationToken ct = default)
    {
        if (!IsAuthorizedRole())
        {
            ErrorMessage = "Unauthorized: Access to Sales History is restricted to Owner, Admin, and Manager.";
            Sales.Clear();
            TotalCount = 0;
            TotalPages = 1;
            IsEmpty = true;
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            DateTime? fromUtc = null;
            DateTime? toUtc = null;

            if (IsDateFilterEnabled)
            {
                if (StartDate.HasValue)
                {
                    var startLocal = DateTime.SpecifyKind(StartDate.Value.Date, DateTimeKind.Unspecified);
                    fromUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, _timeZone);
                }

                if (EndDate.HasValue)
                {
                    var endLocal = DateTime.SpecifyKind(EndDate.Value.Date, DateTimeKind.Unspecified);
                    var nextDayLocal = endLocal.AddDays(1);
                    var nextDayUtc = TimeZoneInfo.ConvertTimeToUtc(nextDayLocal, _timeZone);
                    // Empirically proven boundary: include all ticks up to the very last tick before midnight
                    toUtc = nextDayUtc.AddTicks(-1);
                }
            }

            var request = new SaleSearchRequest(
                FromUtc: fromUtc,
                ToUtc: toUtc,
                InvoiceNumber: string.IsNullOrWhiteSpace(SearchInvoiceNumber) ? null : SearchInvoiceNumber.Trim(),
                Page: CurrentPage,
                PageSize: PageSize
            );

            var response = await _posApiClient.SearchSalesAsync(request, ct: ct);

            if (response.Success && response.Data != null)
            {
                Sales.Clear();
                foreach (var item in response.Data.Items)
                {
                    Sales.Add(new SaleSummaryItemViewModel(item, _timeZone));
                }

                TotalCount = response.Data.TotalCount;
                TotalPages = Math.Max(1, (int)Math.Ceiling((double)response.Data.TotalCount / Math.Max(1, response.Data.PageSize)));
                IsEmpty = TotalCount == 0;

                // If currently selected sale is still in list, keep it; otherwise select first or null
                if (Sales.Count > 0)
                {
                    var first = Sales[0];
                    await SelectSaleAsync(first, ct);
                }
                else
                {
                    SelectedSale = null;
                    SelectedSaleDetail = null;
                }
            }
            else
            {
                ErrorMessage = response.ErrorMessage ?? "Failed to load sales history from server.";
                Sales.Clear();
                TotalCount = 0;
                TotalPages = 1;
                IsEmpty = true;
                SelectedSale = null;
                SelectedSaleDetail = null;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error communicating with store server: {ex.Message}";
            Sales.Clear();
            TotalCount = 0;
            TotalPages = 1;
            IsEmpty = true;
            SelectedSale = null;
            SelectedSaleDetail = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task SelectSaleAsync(SaleSummaryItemViewModel? summaryItem, CancellationToken ct = default)
    {
        SelectedSale = summaryItem;
        if (summaryItem == null)
        {
            SelectedSaleDetail = null;
            return;
        }

        IsDetailLoading = true;
        DetailErrorMessage = string.Empty;
        ReprintMessage = string.Empty;
        ShowDuplicateWarningModal = false;

        try
        {
            var response = await _posApiClient.GetSaleByIdAsync(summaryItem.Id, ct: ct);
            if (response.Success && response.Data != null)
            {
                SelectedSaleDetail = new SaleInvoiceDetailViewModel(response.Data, _timeZone);
            }
            else
            {
                DetailErrorMessage = response.ErrorMessage ?? "Failed to load authoritative invoice details.";
                SelectedSaleDetail = null;
            }
        }
        catch (Exception ex)
        {
            DetailErrorMessage = $"Failed to load invoice details: {ex.Message}";
            SelectedSaleDetail = null;
        }
        finally
        {
            IsDetailLoading = false;
        }
    }

    [RelayCommand]
    public async Task ReprintReceiptAsync(CancellationToken ct = default)
    {
        if (SelectedSaleDetail == null) return;

        if (!IsAuthorizedRole())
        {
            ReprintStatusSeverity = "Error";
            ReprintMessage = "Unauthorized: Access to reprint receipts is restricted to Owner, Admin, and Manager.";
            return;
        }

        IsReprinting = true;
        ReprintMessage = "Sending reprint request to store server...";
        ReprintStatusSeverity = "Info";
        ShowDuplicateWarningModal = false;

        try
        {
            var response = await _posApiClient.ReprintReceiptAsync(SelectedSaleDetail.Id, ct: ct);
            HandleReprintResponse(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // Unknown outcome: network timeout or transport failure
            // The physical print might have started or succeeded, but the client never got an HTTP response
            PendingRetryInvoiceId = SelectedSaleDetail.Id;
            ReprintStatusSeverity = "Warning";
            ReprintMessage = "Network timeout or connection lost. Physical receipt print status is UNKNOWN.";
            DuplicateWarningMessage = "The server request timed out or connection was interrupted. A receipt may have already been printed by the physical hardware. Please inspect the printer. If you confirm, a DUPLICATE receipt will be requested.";
            ShowDuplicateWarningModal = true;
        }
        catch (Exception ex)
        {
            ReprintStatusSeverity = "Error";
            ReprintMessage = $"Reprint request failed: {ex.Message}";
        }
        finally
        {
            IsReprinting = false;
        }
    }

    [RelayCommand]
    public async Task ConfirmAndRetryReprintAsync(CancellationToken ct = default)
    {
        // Deliberate operator confirmation: dispatches exactly ONE additional reprint POST
        ShowDuplicateWarningModal = false;
        if (!PendingRetryInvoiceId.HasValue) return;

        var invoiceId = PendingRetryInvoiceId.Value;
        PendingRetryInvoiceId = null;

        if (!IsAuthorizedRole())
        {
            ReprintStatusSeverity = "Error";
            ReprintMessage = "Unauthorized: Access to reprint receipts is restricted to Owner, Admin, and Manager.";
            return;
        }

        IsReprinting = true;
        ReprintMessage = "Sending operator-confirmed duplicate reprint request...";
        ReprintStatusSeverity = "Info";

        try
        {
            var response = await _posApiClient.ReprintReceiptAsync(invoiceId, ct: ct);
            HandleReprintResponse(response, ct);
        }
        catch (Exception ex)
        {
            ReprintStatusSeverity = "Error";
            ReprintMessage = $"Reprint attempt failed: {ex.Message}";
        }
        finally
        {
            IsReprinting = false;
        }
    }

    [RelayCommand]
    public void CancelDuplicateRetry()
    {
        ShowDuplicateWarningModal = false;
        PendingRetryInvoiceId = null;
        ReprintStatusSeverity = "Info";
        ReprintMessage = "Reprint retry cancelled by operator. No duplicate request was sent.";
    }

    private void HandleReprintResponse(ApiResponse<RetailPOS.Contracts.Hardware.ReceiptPrintResponseDto> response, CancellationToken ct)
    {
        if (response.Success && response.Data != null)
        {
            if (response.Data.Status == PrintStatus.Success)
            {
                ReprintStatusSeverity = "Success";
                ReprintMessage = $"Receipt reprinted successfully (Reprint #{response.Data.ReprintCount}).";

                // Refresh authoritative invoice metadata
                if (SelectedSaleDetail != null && SelectedSaleDetail.Id == response.Data.InvoiceId)
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            var refresh = await _posApiClient.GetSaleByIdAsync(response.Data.InvoiceId, ct: ct);
                            if (refresh.Success && refresh.Data != null)
                            {
                                SelectedSaleDetail.UpdateReprintMetadata(refresh.Data.ReprintCount, refresh.Data.LastReprintedAtUtc);
                            }
                            else
                            {
                                ReprintMessage = $"Receipt reprinted successfully (Reprint #{response.Data.ReprintCount}), but invoice metadata could not be refreshed.";
                            }
                        }
                        catch
                        {
                            ReprintMessage = $"Receipt reprinted successfully (Reprint #{response.Data.ReprintCount}), but invoice metadata could not be refreshed.";
                        }
                    }, ct);
                }
            }
            else if (response.Data.Status == PrintStatus.Failed)
            {
                ReprintStatusSeverity = "Error";
                ReprintMessage = $"Printer reported failure: {response.Data.ErrorMessage ?? "Hardware error."}";
            }
            else
            {
                ReprintStatusSeverity = "Warning";
                ReprintMessage = $"Reprint status: {response.Data.Status}. {response.Data.ErrorMessage}";
            }
        }
        else
        {
            if (response.ErrorCode is "UNAUTHORIZED" or "FORBIDDEN")
            {
                ReprintStatusSeverity = "Error";
                ReprintMessage = "Authorization failed: You do not have permission to reprint receipts.";
            }
            else
            {
                ReprintStatusSeverity = "Error";
                ReprintMessage = $"Server error: {response.ErrorMessage ?? "Reprint operation failed."}";
            }
        }
    }

    private static bool IsAuthorizedRole()
    {
        var role = ClientSessionManager.Instance.CurrentRole;
        return role is RoleType.Owner or RoleType.Admin or RoleType.Manager;
    }

    public static bool TryResolveTimeZone(string timeZoneId, out TimeZoneInfo resolved)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            resolved = TimeZoneInfo.Utc;
            return false;
        }

        try
        {
            resolved = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch
        {
            // Try standard IANA / Windows aliases
            var normalized = timeZoneId.Trim();
            if (normalized.Equals("Asia/Kolkata", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Asia/Calcutta", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("IST", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    resolved = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                    return true;
                }
                catch { }
            }
            else if (normalized.Equals("UTC", StringComparison.OrdinalIgnoreCase) ||
                     normalized.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase) ||
                     normalized.Equals("GMT", StringComparison.OrdinalIgnoreCase))
            {
                resolved = TimeZoneInfo.Utc;
                return true;
            }

            resolved = TimeZoneInfo.Utc;
            return false;
        }
    }
}
