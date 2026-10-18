using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Client.Services;
using RetailPOS.Client.ViewModels;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Reports;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Client.Tests;

[Collection("ClientSessionTests")]
public sealed class ReportsWorkspaceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Handler { get; set; }
        public List<HttpRequestMessage> CapturedRequests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequests.Add(request);
            if (Handler != null)
            {
                return await Handler(request);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static void SetupSession(RoleType role, Guid? userId = null, Guid? terminalId = null)
    {
        var session = ClientSessionManager.Instance;
        session.ClearSession();
        var login = new LoginResponse(
            "test-token-reports-123",
            userId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "test_user",
            "Test Operator",
            role,
            terminalId ?? Guid.Parse("22222222-2222-2222-2222-222222222222"),
            DateTime.UtcNow.AddHours(8));
        session.SetActiveSession(login);
    }

    [Theory]
    [InlineData(0, "₹0.00")]
    [InlineData(50, "₹0.50")]
    [InlineData(100, "₹1.00")]
    [InlineData(125050, "₹1,250.50")]
    [InlineData(-50000, "-₹500.00")]
    [InlineData(10000000000, "₹100,000,000.00")]
    public void CurrencyFormatting_FormatsIntegerPaiseToDecimalRupees(long paise, string expectedFormatted)
    {
        var formatted = DailySalesSummaryViewModel.FormatCurrency(paise);
        Assert.Equal(expectedFormatted, formatted);
    }

    [Fact]
    public void CashReconciliationFormula_CalculationMatchesAuthoritativeEquation()
    {
        var vm = new ShiftReconciliationViewModel
        {
            OpeningFloatPaise = 500000,          // ₹5,000.00
            TotalCashSalesPaise = 2500000,       // ₹25,000.00
            TotalCashRepaymentsPaise = 200000,   // ₹2,000.00
            TotalPayInsPaise = 100000,           // ₹1,000.00
            TotalCashRefundsPaise = 50000,       // ₹500.00
            TotalPayOutsPaise = 150000,          // ₹1,500.00
            TotalCashDropsPaise = 1000000        // ₹10,000.00
        };

        // Expected Cash Formula:
        // Opening (5,000) + Sales (25,000) + Repayments (2,000) + PayIns (1,000) - Refunds (500) - PayOuts (1,500) - Drops (10,000) = 21,000 (2,100,000 paise)
        long expectedPaise = vm.OpeningFloatPaise
            + vm.TotalCashSalesPaise
            + vm.TotalCashRepaymentsPaise
            + vm.TotalPayInsPaise
            - vm.TotalCashRefundsPaise
            - vm.TotalPayOutsPaise
            - vm.TotalCashDropsPaise;

        Assert.Equal(2100000, expectedPaise);

        vm.CurrentExpectedCashPaise = expectedPaise;

        // Test 1: Exact Match (Balanced)
        vm.CountedCashInput = "21000";
        Assert.Equal(0, vm.DiscrepancyPaise);
        Assert.Equal("Exact Match (Balanced)", vm.DiscrepancyStatus);

        // Test 2: Cash Shortage (Negative Discrepancy)
        vm.CountedCashInput = "20500"; // ₹500 short
        Assert.Equal(-50000, vm.DiscrepancyPaise);
        Assert.Equal("Cash Shortage (-₹500.00)", vm.DiscrepancyStatus);

        // Test 3: Cash Overage (Positive Discrepancy)
        vm.CountedCashInput = "21250.50"; // ₹250.50 over
        Assert.Equal(25050, vm.DiscrepancyPaise);
        Assert.Equal("Cash Overage (+₹250.50)", vm.DiscrepancyStatus);
    }

    [Fact]
    public void DatePresets_CalculateCorrectStoreCalendarDates()
    {
        var vm = new ReportsViewModel();
        var today = DateTime.Today;

        // Preset 0: Today
        vm.SelectedDatePresetIndex = 0;
        var (from0, to0) = vm.GetEffectiveDateRange();
        Assert.Equal(today, from0);
        Assert.Equal(today, to0);

        // Preset 1: Yesterday
        vm.SelectedDatePresetIndex = 1;
        var (from1, to1) = vm.GetEffectiveDateRange();
        Assert.Equal(today.AddDays(-1), from1);
        Assert.Equal(today.AddDays(-1), to1);

        // Preset 2: Last 7 Days
        vm.SelectedDatePresetIndex = 2;
        var (from2, to2) = vm.GetEffectiveDateRange();
        Assert.Equal(today.AddDays(-6), from2);
        Assert.Equal(today, to2);

        // Preset 3: This Month
        vm.SelectedDatePresetIndex = 3;
        var (from3, to3) = vm.GetEffectiveDateRange();
        Assert.Equal(new DateTime(today.Year, today.Month, 1), from3);
        Assert.Equal(today, to3);

        // Preset 4: Last Month
        vm.SelectedDatePresetIndex = 4;
        var (from4, to4) = vm.GetEffectiveDateRange();
        var firstOfCurrent = new DateTime(today.Year, today.Month, 1);
        var lastOfPrev = firstOfCurrent.AddDays(-1);
        var firstOfPrev = new DateTime(lastOfPrev.Year, lastOfPrev.Month, 1);
        Assert.Equal(firstOfPrev, from4);
        Assert.Equal(lastOfPrev, to4);

        // Preset 5: Custom Range
        vm.SelectedDatePresetIndex = 5;
        var customFrom = new DateTime(2026, 3, 1);
        var customTo = new DateTime(2026, 3, 15);
        vm.CustomStartDate = new DateTimeOffset(customFrom, TimeSpan.Zero);
        vm.CustomEndDate = new DateTimeOffset(customTo, TimeSpan.Zero);
        var (from5, to5) = vm.GetEffectiveDateRange();
        Assert.Equal(customFrom, from5);
        Assert.Equal(customTo, to5);
    }

    [Fact]
    public void RoleMatrix_OwnerAdminManager_HaveFullAccessAndExports()
    {
        foreach (var role in new[] { RoleType.Owner, RoleType.Admin, RoleType.Manager })
        {
            SetupSession(role);
            using var vm = new ReportsViewModel();

            Assert.True(vm.IsOwnerAdminManager);
            Assert.True(vm.CanViewDailySales);
            Assert.True(vm.CanViewShiftSlips);
            Assert.True(vm.CanViewTaxSummary);
            Assert.True(vm.CanViewProductPerformance);
            Assert.True(vm.CanViewAllCashiers);
            Assert.True(vm.CanViewCashierSummary);
            Assert.True(vm.CanViewTerminalSnapshot);

            Assert.True(vm.CanExportCsv("daily-sales"));
            Assert.True(vm.CanExportCsv("tax-summary"));
            Assert.True(vm.CanExportCsv("product-sales"));
            Assert.True(vm.CanExportCsv("cashier-summary"));
        }
    }

    [Fact]
    public void RoleMatrix_Cashier_StrictlyRestrictedFromDailySales_ShiftSlips_AndExports()
    {
        SetupSession(RoleType.Cashier);
        using var vm = new ReportsViewModel();

        Assert.False(vm.IsOwnerAdminManager);
        Assert.False(vm.CanViewDailySales);
        Assert.False(vm.CanViewShiftSlips);
        Assert.False(vm.CanViewTaxSummary);
        Assert.False(vm.CanViewProductPerformance);
        Assert.False(vm.CanViewAllCashiers);

        // Cashier can view own summary and assigned terminal only
        Assert.True(vm.CanViewCashierSummary);
        Assert.True(vm.CanViewTerminalSnapshot);

        // Default tab for cashier must not be daily sales (0)
        Assert.Equal(5, vm.SelectedTabIndex);

        // Guard: Attempting to select restricted tab (0, 1, 2, 3) snaps back to terminal snapshot (5)
        vm.SelectedTabIndex = 0;
        Assert.Equal(5, vm.SelectedTabIndex);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));

        vm.SelectedTabIndex = 1;
        Assert.Equal(5, vm.SelectedTabIndex);

        vm.SelectedTabIndex = 2;
        Assert.Equal(5, vm.SelectedTabIndex);

        vm.SelectedTabIndex = 3;
        Assert.Equal(5, vm.SelectedTabIndex);

        // CSV Export security invariant: Cashier cannot export general reports
        Assert.False(vm.CanExportCsv("daily-sales"));
        Assert.False(vm.CanExportCsv("tax-summary"));
        Assert.False(vm.CanExportCsv("product-sales"));

        // Cashier can ONLY export their own cashier summary
        Assert.True(vm.CanExportCsv("cashier-summary"));
    }

    [Fact]
    public async Task CashierPerformance_WhenRoleIsCashier_ForcesCurrentUserIdInQuery()
    {
        var cashierUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        SetupSession(RoleType.Cashier, userId: cashierUserId);

        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                var uri = req.RequestUri!.ToString();
                Assert.Contains("/api/v1/reports/cashier-summary", uri);
                Assert.Contains($"cashierUserId={cashierUserId}", uri);

                var dummyDto = new CashierPerformanceReportDto(
                    DateTime.UtcNow.AddDays(-1),
                    DateTime.UtcNow,
                    new List<CashierPerformanceLineDto>
                    {
                        new(cashierUserId, "cashier1", "Cashier One", 1, 10, 500000, 20000, 480000, 0, 300000, 180000, 0)
                    });
                var resp = ApiResponse.Ok(dummyDto);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(resp)
                });
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new ReportsViewModel(client);

        mock.CapturedRequests.Clear();
        vm.SelectedTabIndex = 4; // Cashier Performance tab
        await vm.LoadCurrentTabAsync();

        var cashierRequests = mock.CapturedRequests.FindAll(r => r.RequestUri!.ToString().Contains("/cashier-summary"));
        Assert.NotEmpty(cashierRequests);
        Assert.All(cashierRequests, r => Assert.Contains($"cashierUserId={cashierUserId}", r.RequestUri!.ToString()));
        Assert.Single(vm.Cashier.Cashiers);
        Assert.Equal("cashier1", vm.Cashier.Cashiers[0].Username);
    }

    [Fact]
    public async Task DailySalesSummary_WhenLoadedByManager_MapsAllFinancialAndTenderFields()
    {
        SetupSession(RoleType.Manager);

        var sampleDto = new DailySalesSummaryReportDto(
            FromUtc: DateTime.UtcNow.AddHours(-12),
            ToUtc: DateTime.UtcNow,
            TotalInvoicesCount: 42,
            TotalItemsSoldCount: 120,
            VoidedInvoicesCount: 2,
            VoidedAmountPaise: 15000,
            GrossSubtotalPaise: 450000,
            TotalLineDiscountsPaise: 10000,
            TotalInvoiceDiscountsPaise: 5000,
            TaxableSubtotalPaise: 435000,
            TotalTaxPaise: 78300,
            RoundingAdjustmentPaise: 50,
            GrossSalesPaise: 513350,
            TotalRefundsCount: 1,
            TotalRefundsPaise: 25000,
            NetRevenuePaise: 488350,
            PaymentTenders: new PaymentTenderSummaryDto(
                CashPaise: 250000,
                CardPaise: 150000,
                UpiPaise: 88350,
                BankTransferPaise: 0,
                ChequePaise: 0,
                CreditPaise: 0,
                TotalPaise: 488350),
            HourlyDistribution: new List<HourlySalesDistributionDto>
            {
                new(Hour: 10, InvoiceCount: 15, NetSalesPaise: 200000),
                new(Hour: 14, InvoiceCount: 27, NetSalesPaise: 288350)
            });

        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                var uri = req.RequestUri!.ToString();
                Assert.Contains("/api/v1/reports/daily-sales", uri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(sampleDto))
                });
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new ReportsViewModel(client);

        vm.SelectedTabIndex = 0;
        await vm.LoadCurrentTabAsync();

        Assert.Equal(42, vm.DailySales.TotalInvoicesCount);
        Assert.Equal("₹5,133.50", vm.DailySales.FormattedGrossSales);
        Assert.Equal("₹4,883.50", vm.DailySales.FormattedNetRevenue);
        Assert.Equal("₹2,500.00", vm.DailySales.FormattedCash);
        Assert.Equal("₹1,500.00", vm.DailySales.FormattedCard);
        Assert.Equal("₹883.50", vm.DailySales.FormattedUpi);
        Assert.Equal(2, vm.DailySales.HourlyDistribution.Count);
    }

    [Fact]
    public async Task TaxSummary_WhenLoaded_MapsGstr1CategoriesAndComponents()
    {
        SetupSession(RoleType.Admin);

        var sampleTaxDto = new TaxSummaryReportDto(
            FromUtc: DateTime.UtcNow.AddDays(-7),
            ToUtc: DateTime.UtcNow,
            B2BTaxableSubtotalPaise: 2000000,
            B2CTaxableSubtotalPaise: 3000000,
            TotalTaxableSubtotalPaise: 5000000,
            TotalTaxCollectedPaise: 900000,
            TotalTaxRefundedPaise: 50000,
            NetTaxPayablePaise: 850000,
            Categories: new List<TaxCategoryBreakdownDto>
            {
                new(1, "Standard GST 18%", 5000000, 900000, 277777, 50000, 850000)
            },
            Components: new List<TaxComponentBreakdownDto>
            {
                new("CGST", 9.0m, 450000, 25000, 425000),
                new("SGST", 9.0m, 450000, 25000, 425000)
            });

        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                Assert.Contains("/api/v1/reports/tax-summary", req.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(sampleTaxDto))
                });
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new ReportsViewModel(client);

        vm.SelectedTabIndex = 2; // GST Tax Summary
        await vm.LoadCurrentTabAsync();

        Assert.Equal("₹50,000.00", vm.Tax.FormattedTotalTaxableSubtotal);
        Assert.Equal("₹8,500.00", vm.Tax.FormattedNetTaxPayable);
        Assert.Single(vm.Tax.Categories);
        Assert.Equal(2, vm.Tax.Components.Count);
    }

    [Fact]
    public async Task CsvExport_StreamsDirectlyToDestinationStreamWithoutBuffering()
    {
        SetupSession(RoleType.Manager);
        var csvContent = "InvoiceNumber,Date,GrossAmount,NetAmount\nINV-001,2026-03-01,100.00,100.00\nINV-002,2026-03-01,250.00,250.00";

        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                Assert.Contains("/api/v1/reports/daily-sales/csv", req.RequestUri!.ToString());
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(csvContent, Encoding.UTF8, "text/csv")
                };
                return Task.FromResult(resp);
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new ReportsViewModel(client);
        vm.SelectedTabIndex = 0;

        using var memoryStream = new MemoryStream();
        await vm.ExportCurrentReportCsvAsync(memoryStream);

        memoryStream.Position = 0;
        using var reader = new StreamReader(memoryStream, Encoding.UTF8);
        var resultText = await reader.ReadToEndAsync();

        Assert.Equal(csvContent, resultText);
        Assert.True(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.Equal("Report exported successfully.", vm.StatusMessage);
    }

    [Fact]
    public async Task ShiftReconciliation_PreviewSlips_GeneratesAsciiReceiptSlip()
    {
        var sessionId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();

        var sessionDto = new CashSessionDto(
            Id: sessionId,
            StoreId: 1,
            TerminalId: terminalId,
            CashierUserId: Guid.NewGuid(),
            OpeningTimeUtc: DateTime.UtcNow.AddHours(-4),
            ClosingTimeUtc: null,
            OpeningFloatPaise: 500000,
            TotalCashSalesPaise: 1500000,
            TotalCashRepaymentsPaise: 0,
            TotalCashRefundsPaise: 0,
            TotalPayInsPaise: 0,
            TotalPayOutsPaise: 0,
            TotalCashDropsPaise: 0,
            ExpectedCashPaise: 2000000,
            ClosingCashPaise: null,
            DiscrepancyPaise: null,
            Status: CashSessionStatus.Open,
            ClosingNotes: null,
            IsForceClosed: false,
            ClosedByUserId: null,
            ForceCloseReason: null);

        var reportDto = new CashSessionReportDto(
            Session: sessionDto,
            CashierName: "Sarah Jenkins",
            ClosedByName: null,
            TerminalName: "Counter 01",
            Transactions: new List<CashSessionTransactionDto>
            {
                new(1, sessionId, CashTransactionType.PayIn, 500000, "Float", Guid.NewGuid(), Guid.NewGuid(), null, DateTime.UtcNow.AddHours(-3))
            });

        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                var uri = req.RequestUri!.ToString();
                if (uri.Contains("/x-report"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok(reportDto))
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        var shiftVm = new ShiftReconciliationViewModel
        {
            SessionId = sessionId
        };

        await shiftVm.FetchXReportAsync(client, "test-token");

        Assert.Contains("GROZET RETAILPOS — X-REPORT", shiftVm.SlipPreviewText);
        Assert.Contains("Counter 01", shiftVm.SlipPreviewText);
        Assert.Contains("Sarah Jenkins", shiftVm.SlipPreviewText);
        Assert.Contains("Opening Float", shiftVm.SlipPreviewText);
        Assert.Contains("EXPECTED CASH", shiftVm.SlipPreviewText);
    }
}
