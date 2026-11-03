using System;
using System.IO;
using Microsoft.UI.Text;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RetailPOS.Client.Services;
using RetailPOS.Client.ViewModels;
using RetailPOS.Domain.Enums;
using Windows.UI;

namespace RetailPOS.Client.Views;

public sealed partial class ReportsPage : Page
{
    private static readonly SolidColorBrush ActiveTabBrush = new(Color.FromArgb(255, 30, 62, 98));
    private static readonly SolidColorBrush InactiveTabBrush = new(Color.FromArgb(255, 241, 245, 249));
    private static readonly SolidColorBrush ActiveTextBrush = new(Color.FromArgb(255, 255, 255, 255));
    private static readonly SolidColorBrush InactiveTextBrush = new(Color.FromArgb(255, 51, 65, 85));

    private static readonly SolidColorBrush GreenBrush = new(Color.FromArgb(255, 21, 128, 61));
    private static readonly SolidColorBrush RedBrush = new(Color.FromArgb(255, 220, 38, 38));
    private static readonly SolidColorBrush AmberBrush = new(Color.FromArgb(255, 217, 119, 6));

    private static readonly SolidColorBrush AdminBadgeBg = new(Color.FromArgb(255, 238, 242, 255));
    private static readonly SolidColorBrush AdminBadgeFg = new(Color.FromArgb(255, 79, 70, 229));
    private static readonly SolidColorBrush ManagerBadgeBg = new(Color.FromArgb(255, 240, 253, 244));
    private static readonly SolidColorBrush ManagerBadgeFg = new(Color.FromArgb(255, 22, 101, 52));
    private static readonly SolidColorBrush CashierBadgeBg = new(Color.FromArgb(255, 241, 245, 249));
    private static readonly SolidColorBrush CashierBadgeFg = new(Color.FromArgb(255, 71, 85, 105));

    public ReportsViewModel ViewModel { get; }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(ViewModel.StatusMessage);
    public Visibility LoadingVisibility => ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CustomDateVisibility => ViewModel.IsCustomDateRange ? Visibility.Visible : Visibility.Collapsed;

    public string RoleBadgeText => ViewModel.CurrentRole.ToString().ToUpperInvariant();
    public SolidColorBrush RoleBadgeBackground => ViewModel.CurrentRole switch
    {
        RoleType.Owner or RoleType.Admin => AdminBadgeBg,
        RoleType.Manager => ManagerBadgeBg,
        _ => CashierBadgeBg
    };
    public SolidColorBrush RoleBadgeForeground => ViewModel.CurrentRole switch
    {
        RoleType.Owner or RoleType.Admin => AdminBadgeFg,
        RoleType.Manager => ManagerBadgeFg,
        _ => CashierBadgeFg
    };

    // Tab Headers Visibility
    public Visibility DailySalesTabHeaderVisibility => ViewModel.CanViewDailySales ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShiftTabHeaderVisibility => ViewModel.CanViewShiftSlips ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TaxTabHeaderVisibility => ViewModel.CanViewTaxSummary ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProductsTabHeaderVisibility => ViewModel.CanViewProductPerformance ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CashierTabHeaderVisibility => ViewModel.CanViewCashierSummary ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TerminalTabHeaderVisibility => ViewModel.CanViewTerminalSnapshot ? Visibility.Visible : Visibility.Collapsed;

    // Tab Content Visibilities
    public Visibility Tab0ContentVisibility => ViewModel.SelectedTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Tab1ContentVisibility => ViewModel.SelectedTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Tab2ContentVisibility => ViewModel.SelectedTabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Tab3ContentVisibility => ViewModel.SelectedTabIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Tab4ContentVisibility => ViewModel.SelectedTabIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Tab5ContentVisibility => ViewModel.SelectedTabIndex == 5 ? Visibility.Visible : Visibility.Collapsed;

    // Tab Header Styles
    public SolidColorBrush Tab0Background => ViewModel.SelectedTabIndex == 0 ? ActiveTabBrush : InactiveTabBrush;
    public SolidColorBrush Tab0Foreground => ViewModel.SelectedTabIndex == 0 ? ActiveTextBrush : InactiveTextBrush;
    public FontWeight Tab0FontWeight => ViewModel.SelectedTabIndex == 0 ? FontWeights.Bold : FontWeights.Normal;

    public SolidColorBrush Tab1Background => ViewModel.SelectedTabIndex == 1 ? ActiveTabBrush : InactiveTabBrush;
    public SolidColorBrush Tab1Foreground => ViewModel.SelectedTabIndex == 1 ? ActiveTextBrush : InactiveTextBrush;
    public FontWeight Tab1FontWeight => ViewModel.SelectedTabIndex == 1 ? FontWeights.Bold : FontWeights.Normal;

    public SolidColorBrush Tab2Background => ViewModel.SelectedTabIndex == 2 ? ActiveTabBrush : InactiveTabBrush;
    public SolidColorBrush Tab2Foreground => ViewModel.SelectedTabIndex == 2 ? ActiveTextBrush : InactiveTextBrush;
    public FontWeight Tab2FontWeight => ViewModel.SelectedTabIndex == 2 ? FontWeights.Bold : FontWeights.Normal;

    public SolidColorBrush Tab3Background => ViewModel.SelectedTabIndex == 3 ? ActiveTabBrush : InactiveTabBrush;
    public SolidColorBrush Tab3Foreground => ViewModel.SelectedTabIndex == 3 ? ActiveTextBrush : InactiveTextBrush;
    public FontWeight Tab3FontWeight => ViewModel.SelectedTabIndex == 3 ? FontWeights.Bold : FontWeights.Normal;

    public SolidColorBrush Tab4Background => ViewModel.SelectedTabIndex == 4 ? ActiveTabBrush : InactiveTabBrush;
    public SolidColorBrush Tab4Foreground => ViewModel.SelectedTabIndex == 4 ? ActiveTextBrush : InactiveTextBrush;
    public FontWeight Tab4FontWeight => ViewModel.SelectedTabIndex == 4 ? FontWeights.Bold : FontWeights.Normal;

    public SolidColorBrush Tab5Background => ViewModel.SelectedTabIndex == 5 ? ActiveTabBrush : InactiveTabBrush;
    public SolidColorBrush Tab5Foreground => ViewModel.SelectedTabIndex == 5 ? ActiveTextBrush : InactiveTextBrush;
    public FontWeight Tab5FontWeight => ViewModel.SelectedTabIndex == 5 ? FontWeights.Bold : FontWeights.Normal;

    // Formatted Summaries
    public string InvoicesCountSummary => $"{ViewModel.DailySales.TotalInvoicesCount} invoices billed";
    public string ItemsSoldSummary => $"{ViewModel.DailySales.TotalItemsSoldCount} items sold";
    public string RefundsSummary => $"{ViewModel.DailySales.TotalRefundsCount} refunds, {ViewModel.DailySales.VoidedInvoicesCount} voided";

    public string ShiftTerminalHeader => string.IsNullOrWhiteSpace(ViewModel.Shift.TerminalCode)
        ? "Assigned Terminal"
        : $"{ViewModel.Shift.TerminalCode} — {ViewModel.Shift.TerminalName}";

    public string ShiftCashierAndStartTime =>
        $"Cashier: {ViewModel.Shift.ActiveCashierUsername} | Started: {ViewModel.Shift.FormattedSessionStartTime}";

    public string FormattedTimestamp => ViewModel != null
        ? $"Live Snapshot: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
        : string.Empty;

    public SolidColorBrush DiscrepancyBrush => ViewModel.Shift.DiscrepancyPaise switch
    {
        0 => GreenBrush,
        < 0 => RedBrush,
        _ => AmberBrush
    };

    public Visibility SlipPreviewVisibility =>
        string.IsNullOrWhiteSpace(ViewModel.Shift.SlipPreviewText) ? Visibility.Collapsed : Visibility.Visible;

    public ReportsPage()
    {
        var apiClient = new PosApiClient();
        ViewModel = new ReportsViewModel(apiClient);
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

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.LoadCurrentTabAsync();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.LoadCurrentTabAsync();
    }

    private async void OnExportCsvClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaultFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var (from, to) = ViewModel.GetEffectiveDateRange();
            var reportSlug = ViewModel.SelectedTabIndex switch
            {
                0 => "daily-sales",
                2 => "tax-summary",
                3 => "product-sales",
                4 => "cashier-summary",
                _ => "report"
            };

            var fileName = $"grozet-{reportSlug}-{from:yyyyMMdd}-{to:yyyyMMdd}.csv";
            var filePath = Path.Combine(defaultFolder, fileName);

            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await ViewModel.ExportCurrentReportCsvAsync(fileStream);

            if (string.IsNullOrEmpty(ViewModel.ErrorMessage))
            {
                ViewModel.StatusMessage = $"CSV report successfully saved to {filePath}";
            }
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to export CSV: {ex.Message}";
        }
    }

    private void OnTab0Clicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 0;
        this.Bindings.Update();
    }

    private void OnTab1Clicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 1;
        this.Bindings.Update();
    }

    private void OnTab2Clicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 2;
        this.Bindings.Update();
    }

    private void OnTab3Clicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 3;
        this.Bindings.Update();
    }

    private void OnTab4Clicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 4;
        this.Bindings.Update();
    }

    private void OnTab5Clicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 5;
        this.Bindings.Update();
    }

    private async void OnPreviewXReportClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.GenerateXReportAsync();
        this.Bindings.Update();
    }

    private async void OnPreviewZReportClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.GenerateZReportAsync();
        this.Bindings.Update();
    }
}
