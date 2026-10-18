using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetailPOS.Client.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed partial class ReportsViewModel : ObservableObject, IDisposable
{
    private readonly PosApiClient _posApiClient;
    private CancellationTokenSource? _activeCts;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    // --- Role & Identity Context ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOwnerAdminManager))]
    [NotifyPropertyChangedFor(nameof(CanViewDailySales))]
    [NotifyPropertyChangedFor(nameof(CanViewShiftSlips))]
    [NotifyPropertyChangedFor(nameof(CanViewTaxSummary))]
    [NotifyPropertyChangedFor(nameof(CanViewProductPerformance))]
    [NotifyPropertyChangedFor(nameof(CanViewAllCashiers))]
    [NotifyPropertyChangedFor(nameof(CanViewCashierSummary))]
    [NotifyPropertyChangedFor(nameof(CanViewTerminalSnapshot))]
    public partial RoleType CurrentRole { get; set; } = RoleType.Cashier;

    [ObservableProperty]
    public partial Guid CurrentTerminalId { get; set; }

    [ObservableProperty]
    public partial Guid CurrentUserId { get; set; }

    [ObservableProperty]
    public partial string CurrentUsername { get; set; } = string.Empty;

    public bool IsOwnerAdminManager => CurrentRole is RoleType.Owner or RoleType.Admin or RoleType.Manager;
    public bool CanViewDailySales => IsOwnerAdminManager;
    public bool CanViewShiftSlips => IsOwnerAdminManager;
    public bool CanViewTaxSummary => IsOwnerAdminManager;
    public bool CanViewProductPerformance => IsOwnerAdminManager;
    public bool CanViewAllCashiers => IsOwnerAdminManager;
    public bool CanViewCashierSummary => CurrentRole != RoleType.Viewer;
    public bool CanViewTerminalSnapshot => CurrentRole != RoleType.Viewer;

    // --- Sub-ViewModels ---
    public DailySalesSummaryViewModel DailySales { get; } = new();
    public ShiftReconciliationViewModel Shift { get; } = new();
    public TaxSummaryViewModel Tax { get; } = new();
    public ProductPerformanceViewModel Products { get; } = new();
    public CashierPerformanceViewModel Cashier { get; } = new();

    // --- Date Filtering & Presets ---
    // 0 = Today, 1 = Yesterday, 2 = Last 7 Days, 3 = This Month, 4 = Last Month, 5 = Custom
    [ObservableProperty]
    public partial int SelectedDatePresetIndex { get; set; } = 0;

    [ObservableProperty]
    public partial DateTimeOffset? CustomStartDate { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? CustomEndDate { get; set; }

    public bool IsCustomDateRange => SelectedDatePresetIndex == 5;

    // --- Workspace Tabs ---
    // 0 = Daily Sales, 1 = Shift Reconciliation, 2 = GST Tax Summary, 3 = Product Performance, 4 = Cashier Performance, 5 = Terminal Snapshot
    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; } = 0;

    // --- CSV Export State ---
    [ObservableProperty]
    public partial bool IsExportingCsv { get; set; }

    public ReportsViewModel(PosApiClient? posApiClient = null)
    {
        _posApiClient = posApiClient ?? new PosApiClient();

        var session = ClientSessionManager.Instance;
        CurrentRole = session.CurrentRole ?? RoleType.Cashier;
        CurrentTerminalId = session.CurrentTerminalId ?? Guid.Empty;
        CurrentUserId = session.CurrentUserId ?? Guid.Empty;
        CurrentUsername = session.CurrentUsername ?? string.Empty;

        // Default custom dates to today
        var today = DateTime.Today;
        CustomStartDate = new DateTimeOffset(today);
        CustomEndDate = new DateTimeOffset(today);

        // If Cashier, default to Terminal Snapshot tab (index 5) or Cashier Summary (index 4)
        if (!IsOwnerAdminManager)
        {
            SelectedTabIndex = 5;
        }
    }

    partial void OnSelectedDatePresetIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomDateRange));
        _ = LoadCurrentTabAsync();
    }

    partial void OnCustomStartDateChanged(DateTimeOffset? value)
    {
        if (IsCustomDateRange)
        {
            _ = LoadCurrentTabAsync();
        }
    }

    partial void OnCustomEndDateChanged(DateTimeOffset? value)
    {
        if (IsCustomDateRange)
        {
            _ = LoadCurrentTabAsync();
        }
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        // Enforce RBAC guard on tab selection
        if (!IsOwnerAdminManager && value is 0 or 1 or 2 or 3)
        {
            // Unauthorized access attempt blocked, redirect to Terminal Snapshot
            SelectedTabIndex = 5;
            ErrorMessage = "Daily sales, shift slips, tax, and product reports are restricted to management roles.";
            return;
        }

        _ = LoadCurrentTabAsync();
    }

    public (DateTime fromDate, DateTime toDate) GetEffectiveDateRange()
    {
        var today = DateTime.Today;
        return SelectedDatePresetIndex switch
        {
            0 => (today, today), // Today
            1 => (today.AddDays(-1), today.AddDays(-1)), // Yesterday
            2 => (today.AddDays(-6), today), // Last 7 Days
            3 => (new DateTime(today.Year, today.Month, 1), today), // This Month
            4 => GetLastMonthRange(today), // Last Month
            5 => GetCustomRange(today), // Custom
            _ => (today, today)
        };
    }

    private static (DateTime fromDate, DateTime toDate) GetLastMonthRange(DateTime today)
    {
        var firstOfCurrentMonth = new DateTime(today.Year, today.Month, 1);
        var lastOfPreviousMonth = firstOfCurrentMonth.AddDays(-1);
        var firstOfPreviousMonth = new DateTime(lastOfPreviousMonth.Year, lastOfPreviousMonth.Month, 1);
        return (firstOfPreviousMonth, lastOfPreviousMonth);
    }

    private (DateTime fromDate, DateTime toDate) GetCustomRange(DateTime fallback)
    {
        var from = CustomStartDate?.Date ?? fallback;
        var to = CustomEndDate?.Date ?? fallback;
        if (from > to)
        {
            // Swap if backwards
            (from, to) = (to, from);
        }
        return (from, to);
    }

    [RelayCommand]
    public async Task LoadCurrentTabAsync()
    {
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        var ct = _activeCts.Token;

        IsLoading = true;
        IsEmpty = false;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        try
        {
            var (fromDate, toDate) = GetEffectiveDateRange();
            var token = ClientSessionManager.Instance.CurrentToken;

            switch (SelectedTabIndex)
            {
                case 0: // Daily Sales (Owner/Admin/Manager only)
                    if (!CanViewDailySales)
                    {
                        ErrorMessage = "Access to daily sales is restricted to Owner, Admin, and Manager roles.";
                        return;
                    }
                    var dailyRes = await _posApiClient.GetDailySalesSummaryAsync(fromDate, toDate, null, token, ct);
                    if (!dailyRes.Success || dailyRes.Data == null)
                    {
                        ErrorMessage = dailyRes.ErrorMessage ?? "Failed to load daily sales summary.";
                    }
                    else
                    {
                        DailySales.PopulateFromDto(dailyRes.Data);
                        IsEmpty = dailyRes.Data.TotalInvoicesCount == 0 && dailyRes.Data.TotalRefundsCount == 0;
                    }
                    break;

                case 1: // Shift Reconciliation (Owner/Admin/Manager only)
                    if (!CanViewShiftSlips)
                    {
                        ErrorMessage = "Access to shift reconciliation is restricted to Owner, Admin, and Manager roles.";
                        return;
                    }
                    await Shift.LoadActiveSessionAsync(_posApiClient, CurrentTerminalId, token, ct);
                    break;

                case 2: // GST Tax Summary (Owner/Admin/Manager only)
                    if (!CanViewTaxSummary)
                    {
                        ErrorMessage = "Access to tax summary is restricted to Owner, Admin, and Manager roles.";
                        return;
                    }
                    var taxRes = await _posApiClient.GetTaxSummaryAsync(fromDate, toDate, token, ct);
                    if (!taxRes.Success || taxRes.Data == null)
                    {
                        ErrorMessage = taxRes.ErrorMessage ?? "Failed to load tax summary report.";
                    }
                    else
                    {
                        Tax.PopulateFromDto(taxRes.Data);
                        IsEmpty = taxRes.Data.Categories.Count == 0 && taxRes.Data.Components.Count == 0;
                    }
                    break;

                case 3: // Product Performance (Owner/Admin/Manager only)
                    if (!CanViewProductPerformance)
                    {
                        ErrorMessage = "Access to product performance is restricted to Owner, Admin, and Manager roles.";
                        return;
                    }
                    var prodRes = await _posApiClient.GetProductPerformanceAsync(fromDate, toDate, null, null, 1, 50, token, ct);
                    if (!prodRes.Success || prodRes.Data == null)
                    {
                        ErrorMessage = prodRes.ErrorMessage ?? "Failed to load product performance report.";
                    }
                    else
                    {
                        Products.PopulateFromDto(prodRes.Data);
                        IsEmpty = prodRes.Data.Items.Items.Count == 0;
                    }
                    break;

                case 4: // Cashier Performance
                    // CASHIER SECURITY RULE: Cashier may view ONLY their own summary (force CurrentUserId)
                    Guid? targetUserId = IsOwnerAdminManager ? null : CurrentUserId;
                    var cashierRes = await _posApiClient.GetCashierPerformanceAsync(fromDate, toDate, targetUserId, token, ct);
                    if (!cashierRes.Success || cashierRes.Data == null)
                    {
                        ErrorMessage = cashierRes.ErrorMessage ?? "Failed to load cashier performance report.";
                    }
                    else
                    {
                        Cashier.PopulateFromDto(cashierRes.Data);
                        IsEmpty = Cashier.Cashiers.Count == 0;
                    }
                    break;

                case 5: // Current Terminal Snapshot
                    // Cashier may view only their assigned terminal
                    var termRes = await _posApiClient.GetCurrentTerminalReportAsync(CurrentTerminalId, token, ct);
                    if (!termRes.Success || termRes.Data == null)
                    {
                        ErrorMessage = termRes.ErrorMessage ?? "Failed to load current terminal operational snapshot.";
                    }
                    else
                    {
                        Shift.PopulateFromTerminalDto(termRes.Data);
                        IsEmpty = termRes.Data.TodayBillsCount == 0 && termRes.Data.TodayRefundsCount == 0;
                    }
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Request was cancelled intentionally (e.g. user switched tabs)
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading report: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public bool CanExportCsv(string reportType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportType);
        var norm = reportType.Trim().ToLowerInvariant();

        if (IsOwnerAdminManager)
        {
            return norm is "daily-sales" or "tax-summary" or "product-sales" or "cashier-summary";
        }

        // Cashier role security invariant: strictly allow only their own cashier-summary export
        if (CurrentRole == RoleType.Cashier)
        {
            return norm is "cashier-summary";
        }

        return false;
    }

    [RelayCommand]
    public async Task ExportCurrentReportCsvAsync(Stream destinationStream)
    {
        ArgumentNullException.ThrowIfNull(destinationStream);

        string reportType = SelectedTabIndex switch
        {
            0 => "daily-sales",
            2 => "tax-summary",
            3 => "product-sales",
            4 => "cashier-summary",
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(reportType))
        {
            ErrorMessage = "CSV export is not supported for this report view.";
            return;
        }

        if (!CanExportCsv(reportType))
        {
            ErrorMessage = "You are not authorized to export this report to CSV.";
            return;
        }

        IsExportingCsv = true;
        StatusMessage = "Streaming CSV export...";
        ErrorMessage = string.Empty;

        try
        {
            var (fromDate, toDate) = GetEffectiveDateRange();
            var token = ClientSessionManager.Instance.CurrentToken;
            Guid? targetUserId = IsOwnerAdminManager ? null : CurrentUserId;

            var result = await _posApiClient.DownloadReportCsvAsync(
                reportType: reportType,
                fromDate: fromDate,
                toDate: toDate,
                terminalId: SelectedTabIndex == 0 ? null : (Guid?)CurrentTerminalId,
                cashierUserId: targetUserId,
                destinationStream: destinationStream,
                token: token);

            if (!result.Success)
            {
                ErrorMessage = result.ErrorMessage ?? "CSV export failed.";
                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = "Report exported successfully.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"CSV export error: {ex.Message}";
            StatusMessage = string.Empty;
        }
        finally
        {
            IsExportingCsv = false;
        }
    }

    [RelayCommand]
    public async Task GenerateXReportAsync()
    {
        if (!CanViewShiftSlips)
        {
            ErrorMessage = "X-Report generation is restricted to Owner, Admin, and Manager roles.";
            return;
        }

        var token = ClientSessionManager.Instance.CurrentToken;
        await Shift.FetchXReportAsync(_posApiClient, token);
    }

    [RelayCommand]
    public async Task GenerateZReportAsync()
    {
        if (!CanViewShiftSlips)
        {
            ErrorMessage = "Z-Report generation is restricted to Owner, Admin, and Manager roles.";
            return;
        }

        var token = ClientSessionManager.Instance.CurrentToken;
        await Shift.FetchZReportAsync(_posApiClient, token);
    }

    public void Dispose()
    {
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _activeCts = null;
    }
}
