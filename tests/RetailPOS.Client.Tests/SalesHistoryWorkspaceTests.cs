using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Client.Services;
using RetailPOS.Client.ViewModels;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Configuration;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Client.Tests;

[Collection("ClientSessionTests")]
public sealed class SalesHistoryWorkspaceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Handler { get; set; }
        public List<HttpRequestMessage> CapturedRequests { get; } = [];

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

    private static void SetupAuthenticatedSession(RoleType role = RoleType.Manager)
    {
        var session = ClientSessionManager.Instance;
        session.ClearSession();
        var login = new LoginResponse(
            "test-history-token-xyz",
            Guid.NewGuid(),
            "mgr1",
            "Manager Test",
            role,
            Guid.NewGuid(),
            DateTime.UtcNow.AddHours(8));
        session.SetActiveSession(login);
    }

    private static SaleInvoiceDto CreateSampleInvoiceDto(
        Guid id,
        string invoiceNumber = "INV-20261017-0001",
        string cashierName = "Authoritative Staff",
        int reprintCount = 0,
        DateTime? lastReprintedAtUtc = null)
    {
        return new SaleInvoiceDto(
            Id: id,
            InvoiceNumber: invoiceNumber,
            ClientTransactionId: Guid.NewGuid(),
            TerminalId: Guid.NewGuid(),
            CashierId: Guid.NewGuid(),
            CashierName: cashierName,
            CustomerId: null,
            Status: SaleStatus.Completed,
            CreatedAtUtc: DateTime.UtcNow.AddMinutes(-30),
            GrossSubtotalPaise: 50000,
            TotalLineDiscountsPaise: 0,
            InvoiceDiscountPaise: 0,
            TaxableSubtotalPaise: 45000,
            TotalTaxPaise: 5000,
            RoundingAdjustmentPaise: 0,
            FinalPayablePaise: 50000,
            Items:
            [
                new SaleItemDto(
                    1,
                    Guid.NewGuid(),
                    "SKU-ITEM1",
                    "Premium Coffee Beans",
                    2,
                    "KG",
                    25000,
                    50000,
                    0,
                    45000,
                    5000,
                    50000,
                    [new TaxComponentBreakdownDto("CGST", 5.0m, 2500), new TaxComponentBreakdownDto("SGST", 5.0m, 2500)])
            ],
            Payments:
            [
                new PaymentRecordDto(Guid.NewGuid(), PaymentMethod.Cash, 50000, 50000, 0, null)
            ],
            ReprintCount: reprintCount,
            LastReprintedAtUtc: lastReprintedAtUtc
        );
    }

    // =========================================================================
    // 1. REPRINT INSTRUMENTATION TESTS (MANDATORY REQUIREMENT 4)
    // =========================================================================

    [Fact]
    public async Task ReprintTimeout_DispatchesExactlyOnePost_AndPreservesUnknownOutcomeWithoutAutoRetry()
    {
        SetupAuthenticatedSession(RoleType.Manager);
        var mockHandler = new MockHttpHandler();
        var saleId = Guid.NewGuid();
        var invoiceDto = CreateSampleInvoiceDto(saleId);

        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}/reprint"))
            {
                // Simulate network timeout or transport interruption
                throw new TaskCanceledException("The request was canceled due to timeout.");
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(invoiceDto), options: JsonOptions)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var apiClient = new PosApiClient(new HttpClient(mockHandler), "https://localhost:5101");
        var vm = new SalesHistoryViewModel(apiClient);

        // Select the invoice detail
        await vm.SelectSaleAsync(new SaleSummaryItemViewModel(new SaleSummaryDto(
            saleId, "INV-20261017-0001", Guid.NewGuid(), Guid.NewGuid(), 50000, SaleStatus.Completed, DateTime.UtcNow, 1, "Cash: ₹500.00")));

        Assert.NotNull(vm.SelectedSaleDetail);

        // Act: Execute reprint request
        await vm.ReprintReceiptAsync();

        // Assert 1: Exactly ONE POST was dispatched
        var postRequests = mockHandler.CapturedRequests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Single(postRequests);
        Assert.Contains($"/api/v1/sales/{saleId}/reprint", postRequests[0].RequestUri!.AbsolutePath);

        // Assert 2: Unknown outcome modal is shown with duplicate warning
        Assert.True(vm.ShowDuplicateWarningModal);
        Assert.Equal("Warning", vm.ReprintStatusSeverity);
        Assert.Contains("UNKNOWN", vm.ReprintMessage);
        Assert.Equal(saleId, vm.PendingRetryInvoiceId);

        // Assert 3: No automatic background retry occurs
        await Task.Delay(50);
        var postRequestsAfterDelay = mockHandler.CapturedRequests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Single(postRequestsAfterDelay);
    }

    [Fact]
    public async Task FailedInvoiceRefresh_PreservesUnknownOutcome_AndDispatchesNoPost()
    {
        SetupAuthenticatedSession(RoleType.Manager);
        var mockHandler = new MockHttpHandler();
        var saleId = Guid.NewGuid();
        var invoiceDto = CreateSampleInvoiceDto(saleId);

        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}/reprint"))
            {
                throw new HttpRequestException("Connection dropped by peer.");
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(invoiceDto), options: JsonOptions)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var apiClient = new PosApiClient(new HttpClient(mockHandler), "https://localhost:5101");
        var vm = new SalesHistoryViewModel(apiClient);

        await vm.SelectSaleAsync(new SaleSummaryItemViewModel(new SaleSummaryDto(
            saleId, "INV-20261017-0001", Guid.NewGuid(), Guid.NewGuid(), 50000, SaleStatus.Completed, DateTime.UtcNow, 1, "Cash: ₹500.00")));

        await vm.ReprintReceiptAsync();

        var initialPostCount = mockHandler.CapturedRequests.Count(r => r.Method == HttpMethod.Post);
        Assert.Equal(1, initialPostCount);
        Assert.True(vm.ShowDuplicateWarningModal);

        // Simulate subsequent invoice refresh failing
        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        // Try selecting again or refreshing detail
        await vm.SelectSaleAsync(new SaleSummaryItemViewModel(new SaleSummaryDto(
            saleId, "INV-20261017-0001", Guid.NewGuid(), Guid.NewGuid(), 50000, SaleStatus.Completed, DateTime.UtcNow, 1, "Cash: ₹500.00")));

        // Zero additional POST requests must be dispatched
        var finalPostCount = mockHandler.CapturedRequests.Count(r => r.Method == HttpMethod.Post);
        Assert.Equal(1, finalPostCount);
    }

    [Fact]
    public async Task ExplicitOperatorConfirmation_DispatchesExactlyOneAdditionalPost_AndNoThirdPostOrLoop()
    {
        SetupAuthenticatedSession(RoleType.Owner);
        var mockHandler = new MockHttpHandler();
        var saleId = Guid.NewGuid();
        var invoiceDto = CreateSampleInvoiceDto(saleId);

        var reprintAttempts = 0;
        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}/reprint"))
            {
                reprintAttempts++;
                if (reprintAttempts == 1)
                {
                    // First request times out
                    throw new TaskCanceledException("Timeout");
                }
                // Second request succeeds
                var responseDto = new ReceiptPrintResponseDto(saleId, "INV-20261017-0001", PrintStatus.Success, null, true, 1, DateTime.UtcNow);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(responseDto), options: JsonOptions)
                });
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}"))
            {
                var updatedInvoice = CreateSampleInvoiceDto(saleId, reprintCount: 1, lastReprintedAtUtc: DateTime.UtcNow);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(updatedInvoice), options: JsonOptions)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var apiClient = new PosApiClient(new HttpClient(mockHandler), "https://localhost:5101");
        var vm = new SalesHistoryViewModel(apiClient);

        await vm.SelectSaleAsync(new SaleSummaryItemViewModel(new SaleSummaryDto(
            saleId, "INV-20261017-0001", Guid.NewGuid(), Guid.NewGuid(), 50000, SaleStatus.Completed, DateTime.UtcNow, 1, "Cash: ₹500.00")));

        // 1. Initial attempt times out
        await vm.ReprintReceiptAsync();
        Assert.True(vm.ShowDuplicateWarningModal);
        Assert.Equal(1, mockHandler.CapturedRequests.Count(r => r.Method == HttpMethod.Post));

        // 2. Explicit operator confirmation triggers retry
        await vm.ConfirmAndRetryReprintAsync();

        // Assert: Exactly ONE additional POST was dispatched (total 2)
        var totalPosts = mockHandler.CapturedRequests.Count(r => r.Method == HttpMethod.Post);
        Assert.Equal(2, totalPosts);

        // Assert: Modal is closed, retry state cleared
        Assert.False(vm.ShowDuplicateWarningModal);
        Assert.Null(vm.PendingRetryInvoiceId);
        Assert.Equal("Success", vm.ReprintStatusSeverity);
        Assert.Contains("Reprint #1", vm.ReprintMessage);

        // Assert: No third POST or loop occurs
        await Task.Delay(50);
        Assert.Equal(2, mockHandler.CapturedRequests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task ConfirmedSuccess_UpdatesReprintCount_AndRefreshesAuthoritativeMetadata()
    {
        SetupAuthenticatedSession(RoleType.Admin);
        var mockHandler = new MockHttpHandler();
        var saleId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;

        var reprinted = false;
        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}/reprint"))
            {
                reprinted = true;
                var successDto = new ReceiptPrintResponseDto(saleId, "INV-20261017-0001", PrintStatus.Success, null, true, 2, nowUtc);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(successDto), options: JsonOptions)
                });
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}"))
            {
                var returnedInvoice = reprinted
                    ? CreateSampleInvoiceDto(saleId, reprintCount: 2, lastReprintedAtUtc: nowUtc)
                    : CreateSampleInvoiceDto(saleId, reprintCount: 0);

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(returnedInvoice), options: JsonOptions)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var apiClient = new PosApiClient(new HttpClient(mockHandler), "https://localhost:5101");
        var vm = new SalesHistoryViewModel(apiClient);

        await vm.SelectSaleAsync(new SaleSummaryItemViewModel(new SaleSummaryDto(
            saleId, "INV-20261017-0001", Guid.NewGuid(), Guid.NewGuid(), 50000, SaleStatus.Completed, DateTime.UtcNow, 1, "Cash: ₹500.00")));

        Assert.Equal(0, vm.SelectedSaleDetail!.ReprintCount);

        await vm.ReprintReceiptAsync();

        Assert.Equal("Success", vm.ReprintStatusSeverity);
        Assert.Contains("Reprint #2", vm.ReprintMessage);
        Assert.False(vm.ShowDuplicateWarningModal);

        // Allow background refresh task to complete
        await Task.Delay(100);
        Assert.Equal(2, vm.SelectedSaleDetail.ReprintCount);
        Assert.NotNull(vm.SelectedSaleDetail.LastReprintedAtUtc);
    }

    [Fact]
    public async Task ConfirmedServerFailure_PreservesReprintCount_AndDisplaysServerErrorMessage()
    {
        SetupAuthenticatedSession(RoleType.Manager);
        var mockHandler = new MockHttpHandler();
        var saleId = Guid.NewGuid();
        var initialInvoice = CreateSampleInvoiceDto(saleId, reprintCount: 0);

        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}/reprint"))
            {
                var failDto = new ReceiptPrintResponseDto(saleId, "INV-20261017-0001", PrintStatus.Failed, "Printer paper roll exhausted.", true, 0, DateTime.UtcNow);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(failDto), options: JsonOptions)
                });
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(initialInvoice), options: JsonOptions)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var apiClient = new PosApiClient(new HttpClient(mockHandler), "https://localhost:5101");
        var vm = new SalesHistoryViewModel(apiClient);

        await vm.SelectSaleAsync(new SaleSummaryItemViewModel(new SaleSummaryDto(
            saleId, "INV-20261017-0001", Guid.NewGuid(), Guid.NewGuid(), 50000, SaleStatus.Completed, DateTime.UtcNow, 1, "Cash: ₹500.00")));

        await vm.ReprintReceiptAsync();

        Assert.Equal("Error", vm.ReprintStatusSeverity);
        Assert.Contains("Printer paper roll exhausted", vm.ReprintMessage);
        Assert.Equal(0, vm.SelectedSaleDetail!.ReprintCount);
        Assert.False(vm.ShowDuplicateWarningModal);
    }

    [Fact]
    public async Task AuthorizationFailure_ClassifiedAsAuthError_NotPrinterFailure()
    {
        SetupAuthenticatedSession(RoleType.Manager);
        var mockHandler = new MockHttpHandler();
        var saleId = Guid.NewGuid();
        var initialInvoice = CreateSampleInvoiceDto(saleId);

        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}/reprint"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = JsonContent.Create(ApiResponse.Fail<ReceiptPrintResponseDto>("Permission denied.", "FORBIDDEN"), options: JsonOptions)
                });
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains($"/sales/{saleId}"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(initialInvoice), options: JsonOptions)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var apiClient = new PosApiClient(new HttpClient(mockHandler), "https://localhost:5101");
        var vm = new SalesHistoryViewModel(apiClient);

        await vm.SelectSaleAsync(new SaleSummaryItemViewModel(new SaleSummaryDto(
            saleId, "INV-20261017-0001", Guid.NewGuid(), Guid.NewGuid(), 50000, SaleStatus.Completed, DateTime.UtcNow, 1, "Cash: ₹500.00")));

        await vm.ReprintReceiptAsync();

        Assert.Equal("Error", vm.ReprintStatusSeverity);
        Assert.Contains("permission", vm.ReprintMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.ShowDuplicateWarningModal);
    }

    [Fact]
    public void OperatorCancel_DismissesModal_AndSendsNoRequest()
    {
        SetupAuthenticatedSession(RoleType.Manager);
        var mockHandler = new MockHttpHandler();
        var apiClient = new PosApiClient(new HttpClient(mockHandler), "https://localhost:5101");
        var vm = new SalesHistoryViewModel(apiClient);

        // Manually place in unknown outcome state
        vm.ReprintStatusSeverity = "Warning";
        vm.ShowDuplicateWarningModal = true;
        vm.DuplicateWarningMessage = "Warning modal active.";

        vm.CancelDuplicateRetry();

        Assert.False(vm.ShowDuplicateWarningModal);
        Assert.Null(vm.PendingRetryInvoiceId);
        Assert.Contains("cancelled", vm.ReprintMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mockHandler.CapturedRequests);
    }

    // =========================================================================
    // 2. SECURITY & ROLE GUARDS TESTS
    // =========================================================================

    [Theory]
    [InlineData(RoleType.Cashier)]
    [InlineData(RoleType.Viewer)]
    public async Task RoleGuards_CashierAndViewer_AreForbiddenFromAccessingSalesHistory(RoleType forbiddenRole)
    {
        SetupAuthenticatedSession(forbiddenRole);
        var mockHandler = new MockHttpHandler();
        var apiClient = new PosApiClient(new HttpClient(mockHandler), "https://localhost:5101");
        var vm = new SalesHistoryViewModel(apiClient);

        // Act: Attempt to initialize or load sales
        await vm.InitializeAsync();
        await vm.LoadSalesAsync();

        // Assert: Access is denied, error is set, zero requests sent
        Assert.Contains("Unauthorized", vm.ErrorMessage);
        Assert.Empty(vm.Sales);
        Assert.Empty(mockHandler.CapturedRequests);
    }

    [Fact]
    public void NoFabricatedCashierOrTerminalNames_PreservesAuthoritativeContractData()
    {
        var id = Guid.NewGuid();
        var invoiceDto = new SaleInvoiceDto(
            Id: id,
            InvoiceNumber: "INV-AUTH-001",
            ClientTransactionId: Guid.NewGuid(),
            TerminalId: Guid.NewGuid(),
            CashierId: Guid.NewGuid(),
            CashierName: "Priya Sharma",
            CustomerId: Guid.NewGuid(),
            Status: SaleStatus.Completed,
            CreatedAtUtc: DateTime.UtcNow,
            GrossSubtotalPaise: 10000,
            TotalLineDiscountsPaise: 0,
            InvoiceDiscountPaise: 0,
            TaxableSubtotalPaise: 10000,
            TotalTaxPaise: 0,
            RoundingAdjustmentPaise: 0,
            FinalPayablePaise: 10000,
            Items: [],
            Payments: []
        );

        var vm = new SaleInvoiceDetailViewModel(invoiceDto, TimeZoneInfo.Utc);

        // Assert: Authoritative cashier name is shown exactly as supplied
        Assert.Equal("Priya Sharma", vm.CashierDisplayName);

        // When CashierName is empty, fallback is generic label "Cashier", NOT a fabricated name like "John Doe"
        var anonymousInvoice = invoiceDto with { CashierName = "" };
        var anonVm = new SaleInvoiceDetailViewModel(anonymousInvoice, TimeZoneInfo.Utc);
        Assert.Equal("Cashier", anonVm.CashierDisplayName);
    }

    // =========================================================================
    // 3. STORE TIMEZONE & DATE BOUNDARY TESTS
    // =========================================================================

    [Fact]
    public async Task StoreTimeZone_WhenResolutionFails_DisablesDateFilter_AndDisplaysWarning()
    {
        SetupAuthenticatedSession(RoleType.Manager);
        var mockHandler = new MockHttpHandler();

        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/v1/store/settings")
            {
                var settings = new StoreSettingsDto(1, "Test Store", null, null, null, null, PricingModel.TaxInclusive, NegativeStockPolicy.Allowed, true, "UNKNOWN_NONEXISTENT_TIMEZONE");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(settings), options: JsonOptions)
                });
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/v1/sales")
            {
                var paged = new PagedResult<SaleSummaryDto>([], 0, 1, 20);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(paged), options: JsonOptions)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var apiClient = new PosApiClient(new HttpClient(mockHandler), "https://localhost:5101");
        var vm = new SalesHistoryViewModel(apiClient);

        await vm.InitializeAsync();

        // Assert: Date filtering is disabled with actionable warning, invoice search remains available
        Assert.False(vm.IsDateFilterEnabled);
        Assert.Contains("could not be resolved", vm.WarningMessage);
        Assert.Contains("Date range filtering is disabled", vm.WarningMessage);
    }

    [Fact]
    public async Task StoreTimeZone_WhenStoreSettingsEndpointFails_DefaultsToIndiaStandardTimeWithWarning()
    {
        SetupAuthenticatedSession(RoleType.Manager);
        var mockHandler = new MockHttpHandler();

        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/v1/store/settings")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/v1/sales")
            {
                var paged = new PagedResult<SaleSummaryDto>([], 0, 1, 20);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(paged), options: JsonOptions)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var apiClient = new PosApiClient(new HttpClient(mockHandler), "https://localhost:5101");
        var vm = new SalesHistoryViewModel(apiClient);

        await vm.InitializeAsync();

        // Assert: Defaulted to India Standard Time with visible warning
        Assert.True(vm.IsDateFilterEnabled);
        Assert.Contains("India Standard Time", vm.WarningMessage);
        Assert.Equal("India Standard Time", vm.StoreTimeZone.Id);
    }

    [Fact]
    public async Task DateBoundary_UsesNextDayAddTicksMinusOne_ForInclusiveFiltering()
    {
        SetupAuthenticatedSession(RoleType.Manager);
        var mockHandler = new MockHttpHandler();

        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/v1/store/settings")
            {
                var settings = new StoreSettingsDto(1, "Test Store", null, null, null, null, PricingModel.TaxInclusive, NegativeStockPolicy.Allowed, true, "India Standard Time");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(settings), options: JsonOptions)
                });
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/v1/sales")
            {
                var paged = new PagedResult<SaleSummaryDto>([], 0, 1, 20);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(paged), options: JsonOptions)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var apiClient = new PosApiClient(new HttpClient(mockHandler), "https://localhost:5101");
        var vm = new SalesHistoryViewModel(apiClient);

        await vm.InitializeAsync();

        // Set date filter for 2026-10-15 single day
        vm.StartDate = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        vm.EndDate = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

        await vm.SearchAsync();

        var salesQueryRequest = mockHandler.CapturedRequests.Last(r => r.RequestUri!.AbsolutePath == "/api/v1/sales");
        var query = salesQueryRequest.RequestUri!.Query;

        // Parse query params
        Assert.Contains("fromUtc=", query);
        Assert.Contains("toUtc=", query);

        // Verify toUtc contains sub-second tick precision (.9999999) corresponding to nextDayUtc.AddTicks(-1)
        var queryParts = query.TrimStart('?').Split('&');
        var toUtcPart = queryParts.First(p => p.StartsWith("toUtc=", StringComparison.Ordinal)).Split('=')[1];
        var decodedToUtc = Uri.UnescapeDataString(toUtcPart);

        Assert.Contains(".9999999", decodedToUtc);
    }
}
