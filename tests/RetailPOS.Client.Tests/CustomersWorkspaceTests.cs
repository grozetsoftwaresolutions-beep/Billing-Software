using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
using RetailPOS.Contracts.Customers;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Client.Tests;

[Collection("ClientSessionTests")]
public sealed class CustomersWorkspaceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Handler { get; set; }
        public List<HttpRequestMessage> CapturedRequests { get; } = [];
        public List<RecordCustomerPaymentRequest> CapturedPaymentRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequests.Add(request);
            if (request.Content != null && request.RequestUri!.ToString().Contains("/payments") && request.Method == HttpMethod.Post)
            {
                var copy = await request.Content.ReadFromJsonAsync<RecordCustomerPaymentRequest>(JsonOptions, cancellationToken);
                if (copy != null) CapturedPaymentRequests.Add(copy);
            }
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
            "test-token-customers-xyz",
            Guid.NewGuid(),
            "test_user",
            "Test Operator",
            role,
            Guid.NewGuid(),
            DateTime.UtcNow.AddHours(8));
        session.SetActiveSession(login);
    }

    private static CustomerDto CreateSampleCustomerDto(
        Guid id,
        string name = "Sunita Enterprises",
        string phone = "9876543210",
        long outstandingCreditPaise = 150000,
        long creditBalancePaise = 0,
        long creditLimitPaise = 500000,
        bool isActive = true)
    {
        return new CustomerDto(
            Id: id,
            Name: name,
            Phone: phone,
            Email: "sunita@example.com",
            Address: "123 Market Road, Bangalore",
            Gstin: "29AAAAA0000A1Z5",
            OutstandingCreditPaise: outstandingCreditPaise,
            CreditBalancePaise: creditBalancePaise,
            CreditLimitPaise: creditLimitPaise,
            IsActive: isActive,
            Notes: "Valued commercial customer",
            CreatedAtUtc: DateTime.UtcNow.AddMonths(-3),
            UpdatedAtUtc: DateTime.UtcNow.AddDays(-1));
    }

    [Fact]
    public void CustomerSummaryItemViewModel_FormatsAuthoritativeBalancesCorrectly()
    {
        var dto = CreateSampleCustomerDto(
            Guid.NewGuid(),
            outstandingCreditPaise: 125050,  // ₹1,250.50
            creditBalancePaise: 50000,       // ₹500.00
            creditLimitPaise: 1000000);      // ₹10,000.00

        var vm = new CustomerSummaryItemViewModel(dto);

        // Verify strictly formatted server values
        Assert.Equal("₹1,250.50", vm.OutstandingCreditFormatted);
        Assert.Equal("₹500.00", vm.CreditBalanceFormatted);
        Assert.Equal("₹10,000.00", vm.CreditLimitFormatted);
        Assert.True(vm.HasDebt);
        Assert.True(vm.HasCreditBalance);
        Assert.Equal("Active", vm.StatusText);
        Assert.Equal("S", vm.InitialLetter);

        // Safety Invariant #3: Confirm NO synthetic "AvailableCredit" property exists on ViewModel
        var prop = typeof(CustomerSummaryItemViewModel).GetProperty("AvailableCredit");
        Assert.Null(prop);
        var prop2 = typeof(CustomerSummaryItemViewModel).GetProperty("AvailableCreditPaise");
        Assert.Null(prop2);
    }

    [Theory]
    [InlineData("100", 10000)]
    [InlineData("100.5", 10050)]
    [InlineData("100.50", 10050)]
    [InlineData("0.01", 1)]
    [InlineData("123456.78", 12345678)]
    [InlineData(" 250.00 ", 25000)]
    public void TryParseRupeesToPaise_ValidInputs_ConvertExactly(string input, long expectedPaise)
    {
        var success = CustomersViewModel.TryParseRupeesToPaise(input, out var paise, out var error, allowZero: false);
        Assert.True(success);
        Assert.Empty(error);
        Assert.Equal(expectedPaise, paise);
    }

    [Fact]
    public void TryParseRupeesToPaise_AllowZero_ConvertsZero()
    {
        var success = CustomersViewModel.TryParseRupeesToPaise("0", out var paise, out var error, allowZero: true);
        Assert.True(success);
        Assert.Empty(error);
        Assert.Equal(0, paise);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("100.555")]   // More than 2 decimal digits: REJECTED without rounding!
    [InlineData("100.1234")]  // More than 2 decimal digits: REJECTED!
    [InlineData("-50")]       // Negative: REJECTED!
    [InlineData("-0.01")]     // Negative: REJECTED!
    [InlineData("0")]         // Zero when not allowed: REJECTED!
    [InlineData("1e5")]       // Scientific notation: REJECTED!
    [InlineData("+100")]      // Leading sign: REJECTED!
    [InlineData("$50")]       // Symbol: REJECTED!
    public void TryParseRupeesToPaise_InvalidInputs_StrictlyRejected(string? input)
    {
        var success = CustomersViewModel.TryParseRupeesToPaise(input, out var paise, out var error, allowZero: false);
        Assert.False(success);
        Assert.NotEmpty(error);
        Assert.Equal(0, paise);
    }

    [Fact]
    public async Task SubmitRepayment_InvalidAmount_ProducesZeroHttpRequests()
    {
        SetupAuthenticatedSession(RoleType.Cashier);
        var mockHandler = new MockHttpHandler();
        var httpClient = new HttpClient(mockHandler);
        var posApiClient = new PosApiClient(httpClient, "https://127.0.0.1:5001");
        var vm = new CustomersViewModel(posApiClient);

        var customer = new CustomerSummaryItemViewModel(CreateSampleCustomerDto(Guid.NewGuid()));
        vm.SelectedCustomer = customer;
        vm.OpenRepaymentDialog();

        // Clear captured requests from selecting customer
        mockHandler.CapturedRequests.Clear();

        // Enter invalid amount with 3 decimal digits
        vm.RepaymentAmountRupees = "100.555";
        vm.RepaymentMethod = PaymentMethod.Cash;

        await vm.SubmitRepaymentAsync();

        // Safety Invariant #2: Must make ZERO HTTP requests!
        Assert.Empty(mockHandler.CapturedRequests);
        Assert.NotEmpty(vm.RepaymentValidationError);
        Assert.Contains("up to 2 decimal places", vm.RepaymentValidationError);
    }

    [Fact]
    public async Task SubmitRepayment_CardWithoutReference_ProducesZeroHttpRequests()
    {
        SetupAuthenticatedSession(RoleType.Cashier);
        var mockHandler = new MockHttpHandler();
        var httpClient = new HttpClient(mockHandler);
        var posApiClient = new PosApiClient(httpClient, "https://127.0.0.1:5001");
        var vm = new CustomersViewModel(posApiClient);

        var customer = new CustomerSummaryItemViewModel(CreateSampleCustomerDto(Guid.NewGuid()));
        vm.SelectedCustomer = customer;
        vm.OpenRepaymentDialog();

        // Clear captured requests from selecting customer
        mockHandler.CapturedRequests.Clear();

        vm.RepaymentAmountRupees = "250.00";
        vm.RepaymentMethod = PaymentMethod.Card;
        vm.RepaymentReferenceNumber = ""; // Empty reference for Card

        await vm.SubmitRepaymentAsync();

        // Must make ZERO HTTP requests
        Assert.Empty(mockHandler.CapturedRequests);
        Assert.NotEmpty(vm.RepaymentValidationError);
        Assert.Contains("Reference number is mandatory", vm.RepaymentValidationError);
    }

    [Fact]
    public async Task SubmitRepayment_GeneratesNewClientTransactionId_WhenOperatorInitiates()
    {
        SetupAuthenticatedSession(RoleType.Cashier);
        var customerId = Guid.NewGuid();
        var mockHandler = new MockHttpHandler();
        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                if (req.RequestUri!.ToString().Contains("/ledger"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ApiResponse.Ok<IReadOnlyList<CustomerLedgerEntryDto>>([]), options: JsonOptions) });
                if (req.RequestUri!.ToString().Contains("/payments"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ApiResponse.Ok<IReadOnlyList<CustomerPaymentDto>>([]), options: JsonOptions) });
                if (req.RequestUri!.ToString().Contains("/sales"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ApiResponse.Ok<IReadOnlyList<SaleInvoiceDto>>([]), options: JsonOptions) });

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ApiResponse.Ok(CreateSampleCustomerDto(customerId)), options: JsonOptions) });
            }

            var paymentDto = new CustomerPaymentDto(
                Id: Guid.NewGuid(),
                CustomerId: customerId,
                AmountPaise: 25000,
                Method: PaymentMethod.Cash,
                ReferenceNumber: null,
                PaymentDateUtc: DateTime.UtcNow,
                ClientTransactionId: Guid.NewGuid(),
                Notes: null,
                CreatedByUserId: Guid.NewGuid(),
                CreatedAtUtc: DateTime.UtcNow);

            var apiResp = ApiResponse.Ok(paymentDto);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(apiResp, options: JsonOptions)
            });
        };

        var httpClient = new HttpClient(mockHandler);
        var posApiClient = new PosApiClient(httpClient, "https://127.0.0.1:5001");
        var vm = new CustomersViewModel(posApiClient);

        var customer = new CustomerSummaryItemViewModel(CreateSampleCustomerDto(customerId));
        vm.SelectedCustomer = customer;

        // Operator opens repayment dialog
        vm.OpenRepaymentDialog();
        var generatedId = vm.PendingRepaymentTxId;
        Assert.NotNull(generatedId);
        Assert.NotEqual(Guid.Empty, generatedId.Value);

        vm.RepaymentAmountRupees = "250.00";
        vm.RepaymentMethod = PaymentMethod.Cash;

        await vm.SubmitRepaymentAsync();

        // Verified HTTP POST was sent for the payment
        var postRequests = mockHandler.CapturedRequests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Single(postRequests);
        Assert.Contains($"/api/v1/customers/{customerId}/payments", postRequests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task SubmitRepayment_PreservesSameClientTransactionId_OnNetworkFailureOrAmbiguousOutcome()
    {
        SetupAuthenticatedSession(RoleType.Cashier);
        var customerId = Guid.NewGuid();
        var postCount = 0;

        var mockHandler = new MockHttpHandler();
        mockHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                if (req.RequestUri!.ToString().Contains("/ledger"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ApiResponse.Ok<IReadOnlyList<CustomerLedgerEntryDto>>([]), options: JsonOptions) });
                if (req.RequestUri!.ToString().Contains("/payments"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ApiResponse.Ok<IReadOnlyList<CustomerPaymentDto>>([]), options: JsonOptions) });
                if (req.RequestUri!.ToString().Contains("/sales"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ApiResponse.Ok<IReadOnlyList<SaleInvoiceDto>>([]), options: JsonOptions) });

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ApiResponse.Ok(CreateSampleCustomerDto(customerId)), options: JsonOptions) });
            }

            postCount++;
            if (postCount == 1)
            {
                // First POST request encounters a network drop / timeout!
                throw new HttpRequestException("The connection was forcibly closed by the remote host.");
            }

            // Retry succeeds (server idempotently accepts or returns payment)
            var paymentDto = new CustomerPaymentDto(
                Id: Guid.NewGuid(),
                CustomerId: customerId,
                AmountPaise: 50000,
                Method: PaymentMethod.Cash,
                ReferenceNumber: null,
                PaymentDateUtc: DateTime.UtcNow,
                ClientTransactionId: Guid.NewGuid(),
                Notes: null,
                CreatedByUserId: Guid.NewGuid(),
                CreatedAtUtc: DateTime.UtcNow);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(ApiResponse.Ok(paymentDto), options: JsonOptions)
            });
        };

        var httpClient = new HttpClient(mockHandler);
        var posApiClient = new PosApiClient(httpClient, "https://127.0.0.1:5001");
        var vm = new CustomersViewModel(posApiClient);

        var customer = new CustomerSummaryItemViewModel(CreateSampleCustomerDto(customerId));
        vm.SelectedCustomer = customer;

        vm.OpenRepaymentDialog();
        var initialTxId = vm.PendingRepaymentTxId;
        Assert.NotNull(initialTxId);

        vm.RepaymentAmountRupees = "500.00";
        vm.RepaymentMethod = PaymentMethod.Cash;

        // 1. First attempt fails with network exception
        await vm.SubmitRepaymentAsync();

        Assert.True(vm.IsRepaymentAmbiguous);
        // Safety Invariant #1: PendingRepaymentTxId must be PRESERVED, NOT regenerated!
        Assert.Equal(initialTxId, vm.PendingRepaymentTxId);
        Assert.True(vm.CanRetrySameRepayment);
        Assert.Contains("Connection or timeout error", vm.RepaymentStatusMessage);

        var postRequestsAfterAttempt1 = mockHandler.CapturedRequests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Single(postRequestsAfterAttempt1);
        Assert.Single(mockHandler.CapturedPaymentRequests);
        Assert.Equal(initialTxId.Value, mockHandler.CapturedPaymentRequests[0].ClientTransactionId);

        // 2. Operator clicks Retry (Same ID)
        await vm.RetryRepaymentAsync();

        var postRequestsAfterRetry = mockHandler.CapturedRequests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, postRequestsAfterRetry.Count);
        Assert.Equal(2, mockHandler.CapturedPaymentRequests.Count);

        // Safety Invariant #1 Verified: Second attempt MUST reuse the exact same ClientTransactionId!
        Assert.Equal(initialTxId.Value, mockHandler.CapturedPaymentRequests[1].ClientTransactionId);
        Assert.False(vm.IsRepaymentAmbiguous);
        Assert.Equal("Success", vm.RepaymentStatusSeverity);
    }

    [Fact]
    public async Task SearchCustomersAsync_AppliesSearchQueryAndFilter_PopulatesList()
    {
        SetupAuthenticatedSession(RoleType.Cashier);
        var mockHandler = new MockHttpHandler();
        mockHandler.Handler = req =>
        {
            var paged = new PagedResult<CustomerDto>(
                [
                    CreateSampleCustomerDto(Guid.NewGuid(), "Aarav Sharma"),
                    CreateSampleCustomerDto(Guid.NewGuid(), "Bhavna Patel")
                ],
                TotalCount: 2,
                Page: 1,
                PageSize: 20);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(ApiResponse.Ok(paged), options: JsonOptions)
            });
        };

        var httpClient = new HttpClient(mockHandler);
        var posApiClient = new PosApiClient(httpClient, "https://127.0.0.1:5001");
        var vm = new CustomersViewModel(posApiClient);

        vm.SearchQuery = "Aarav";
        vm.ActiveFilterIndex = 1; // Active only

        await vm.SearchAsync();

        Assert.Equal(2, vm.Customers.Count);
        Assert.Equal(2, vm.TotalCount);
        Assert.False(vm.IsEmpty);
        Assert.Equal("Showing 1–2 of 2", vm.PaginationSummary);

        // Verify query string parameters sent to server
        var reqUri = mockHandler.CapturedRequests[0].RequestUri!.ToString();
        Assert.Contains("searchTerm=Aarav", reqUri);
        Assert.Contains("isActive=true", reqUri);
    }

    [Fact]
    public void RoleRestrictions_Cashier_HasRepayment_NoCreditLimitManagement()
    {
        SetupAuthenticatedSession(RoleType.Cashier);
        var vm = new CustomersViewModel(new PosApiClient());

        Assert.Equal(RoleType.Cashier, vm.CurrentRole);
        Assert.True(vm.CanRecordRepayment);
        Assert.True(vm.CanEditProfile);
        Assert.False(vm.CanManageCreditLimit);
        Assert.False(vm.CanChangeStatus);
    }

    [Fact]
    public void RoleRestrictions_Manager_HasFullManagementCapabilities()
    {
        SetupAuthenticatedSession(RoleType.Manager);
        var vm = new CustomersViewModel(new PosApiClient());

        Assert.Equal(RoleType.Manager, vm.CurrentRole);
        Assert.True(vm.CanRecordRepayment);
        Assert.True(vm.CanEditProfile);
        Assert.True(vm.CanManageCreditLimit);
        Assert.True(vm.CanChangeStatus);
    }

    [Fact]
    public void RoleRestrictions_AdminAndOwner_HaveFullManagementCapabilities()
    {
        SetupAuthenticatedSession(RoleType.Admin);
        var vmAdmin = new CustomersViewModel(new PosApiClient());
        Assert.True(vmAdmin.CanManageCreditLimit);
        Assert.True(vmAdmin.CanChangeStatus);

        SetupAuthenticatedSession(RoleType.Owner);
        var vmOwner = new CustomersViewModel(new PosApiClient());
        Assert.True(vmOwner.CanManageCreditLimit);
        Assert.True(vmOwner.CanChangeStatus);
    }

    [Fact]
    public async Task SaveCreditLimit_EmptyReason_RejectsWithoutHttpRequest()
    {
        SetupAuthenticatedSession(RoleType.Manager);
        var mockHandler = new MockHttpHandler();
        var httpClient = new HttpClient(mockHandler);
        var posApiClient = new PosApiClient(httpClient, "https://127.0.0.1:5001");
        var vm = new CustomersViewModel(posApiClient);

        vm.SelectedCustomer = new CustomerSummaryItemViewModel(CreateSampleCustomerDto(Guid.NewGuid()));
        mockHandler.CapturedRequests.Clear();

        vm.OpenCreditLimitDialog();
        vm.NewCreditLimitRupees = "10000.00";
        vm.CreditLimitReason = "   "; // Empty reason

        await vm.SaveCreditLimitAsync();

        Assert.Empty(mockHandler.CapturedRequests);
        Assert.NotEmpty(vm.CreditLimitValidationError);
        Assert.Contains("Reason is mandatory", vm.CreditLimitValidationError);
    }

    [Fact]
    public async Task SaveStatus_EmptyReason_RejectsWithoutHttpRequest()
    {
        SetupAuthenticatedSession(RoleType.Manager);
        var mockHandler = new MockHttpHandler();
        var httpClient = new HttpClient(mockHandler);
        var posApiClient = new PosApiClient(httpClient, "https://127.0.0.1:5001");
        var vm = new CustomersViewModel(posApiClient);

        vm.SelectedCustomer = new CustomerSummaryItemViewModel(CreateSampleCustomerDto(Guid.NewGuid()));
        mockHandler.CapturedRequests.Clear();

        vm.OpenStatusDialog(false);
        vm.StatusReason = ""; // Empty reason

        await vm.SaveStatusAsync();

        Assert.Empty(mockHandler.CapturedRequests);
        Assert.NotEmpty(vm.StatusValidationError);
        Assert.Contains("Reason is mandatory", vm.StatusValidationError);
    }

    [Fact]
    public async Task VerifyRepayment_FindsRecordedPayment_ResolvesAmbiguousState()
    {
        SetupAuthenticatedSession(RoleType.Cashier);
        var customerId = Guid.NewGuid();
        var targetTxId = Guid.NewGuid();

        var mockHandler = new MockHttpHandler();
        mockHandler.Handler = req =>
        {
            if (req.RequestUri!.ToString().Contains("/payments"))
            {
                var payments = new List<CustomerPaymentDto>
                {
                    new(
                        Id: Guid.NewGuid(),
                        CustomerId: customerId,
                        AmountPaise: 75000,
                        Method: PaymentMethod.UPI,
                        ReferenceNumber: "UPI-REF-9988",
                        PaymentDateUtc: DateTime.UtcNow,
                        ClientTransactionId: targetTxId,
                        Notes: null,
                        CreatedByUserId: Guid.NewGuid(),
                        CreatedAtUtc: DateTime.UtcNow)
                };

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok<IReadOnlyList<CustomerPaymentDto>>(payments), options: JsonOptions)
                });
            }

            // Default fallback
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(ApiResponse.Ok(CreateSampleCustomerDto(customerId)), options: JsonOptions)
            });
        };

        var httpClient = new HttpClient(mockHandler);
        var posApiClient = new PosApiClient(httpClient, "https://127.0.0.1:5001");
        var vm = new CustomersViewModel(posApiClient);

        var customer = new CustomerSummaryItemViewModel(CreateSampleCustomerDto(customerId));
        vm.SelectedCustomer = customer;
        vm.PendingRepaymentTxId = targetTxId;
        vm.IsRepaymentAmbiguous = true;

        await vm.VerifyRepaymentAsync();

        Assert.False(vm.IsRepaymentAmbiguous);
        Assert.Equal("Success", vm.RepaymentStatusSeverity);
        Assert.Contains("Payment confirmed", vm.RepaymentStatusMessage);
    }

    #region Acceptance Script Safety Guard Invariant Tests

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static (int ExitCode, string Output, string Error) RunAcceptanceScript(string arguments)
    {
        var scriptPath = Path.Combine(RepoRoot, "scripts", "acceptance", "Test-Phase17_7PhysicalAcceptance.ps1");
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"& '{scriptPath}' {arguments}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return (process.ExitCode, output, error);
    }

    [Theory]
    [InlineData("https://192.168.1.100:5101")]
    [InlineData("https://production.grozet.in:5101")]
    [InlineData("http://remote-store:5100")]
    public void AcceptanceScript_SafetyGuards_RejectsRemoteUrls(string remoteUrl)
    {
        var result = RunAcceptanceScript($"-HttpsBaseUrl \"{remoteUrl}\"");
        Assert.NotEqual(0, result.ExitCode);
        var combined = result.Output + Environment.NewLine + result.Error;
        Assert.Contains("SAFETY VIOLATION", combined);
        Assert.Contains("Refusing execution against non-local target", combined);
    }

    [Fact]
    public void AcceptanceScript_SafetyGuards_FailsClosedOnMutatingRequests()
    {
        var result = RunAcceptanceScript("-HttpsBaseUrl \"https://localhost:5101\" -AllowMutatingTestOperations -VerifySafeguardsOnly");
        Assert.NotEqual(0, result.ExitCode);
        var combined = result.Output + Environment.NewLine + result.Error;
        Assert.Contains("SAFETY BLOCKER", combined);
        Assert.Contains("The target server's actual underlying database cannot be independently verified", combined);
    }

    [Fact]
    public void AcceptanceScript_SafetyGuards_RejectsCallerSuppliedDataRootAsProofOfIsolation()
    {
        var tempIsolatedDir = Path.Combine(Path.GetTempPath(), "RetailPOS_Isolated_Test");
        var result = RunAcceptanceScript($"-HttpsBaseUrl \"https://localhost:5101\" -AllowMutatingTestOperations -IsolatedDatabaseConfirmation \"CONFIRM_ISOLATED_TEST_DATABASE\" -DataRoot \"{tempIsolatedDir}\" -VerifySafeguardsOnly");
        Assert.NotEqual(0, result.ExitCode);
        var combined = result.Output + Environment.NewLine + result.Error;
        Assert.Contains("SAFETY BLOCKER", combined);
        Assert.Contains("A client-supplied -DataRoot argument or localhost URL cannot guarantee database isolation", combined);
    }

    [Fact]
    public void AcceptanceScript_SafetyGuards_DefaultInvocation_IsNonMutating()
    {
        var result = RunAcceptanceScript("-HttpsBaseUrl \"https://localhost:5101\" -VerifySafeguardsOnly");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SAFETY SAFEGUARDS VERIFIED", result.Output);
        Assert.Contains("live mutating operations are strictly fail-closed", result.Output);
    }

    #endregion
}
