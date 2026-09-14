using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Customers;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class CustomerRoleAuthorizationTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string ownerToken, Guid terminalId, string cashierToken, string managerToken, string adminToken)> SetupRolesAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // 1. Setup Owner
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Retail POS", "pos_owner", "Store Owner", "OwnerPass123!", "1234"));

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "pos_owner", "OwnerPass123!"));
        var ownerToken = (await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // 2. Register Terminal
        var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-T1", "Main Terminal"));
        var terminalId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        // 3. Create Cashier
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "test_cashier", "Cashier User", "CashierPass123!", "1111", RoleType.Cashier));

        // 4. Create Manager
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "test_manager", "Manager User", "ManagerPass123!", "2222", RoleType.Manager));

        // 5. Create Admin
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "test_admin", "Admin User", "AdminPass123!", "3333", RoleType.Admin));

        // Login Cashier
        client.DefaultRequestHeaders.Authorization = null;
        var cashierLogin = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
            "test_cashier", "1111", terminalId));
        var cashierToken = (await cashierLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // Login Manager
        var managerLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "test_manager", "ManagerPass123!"));
        var managerToken = (await managerLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // Login Admin
        var adminLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "test_admin", "AdminPass123!"));
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        return (factory, client, ownerToken, terminalId, cashierToken, managerToken, adminToken);
    }

    [Fact]
    public async Task Cashier_CustomerPermissions_CanCreateAndSearch_CannotUpdateLimitOrDeactivate()
    {
        var (factory, client, _, terminalId, cashierToken, managerToken, _) = await SetupRolesAsync();
        using (factory)
        {
            // Cashier creates customer -> ALLOWED (201 Created)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            var createRes = await client.PostAsJsonAsync("/api/v1/customers", new CreateCustomerRequest(
                "Rohit Sharma", "9876543210", null, null, null, 10000, null));
            var createBody = await createRes.Content.ReadAsStringAsync();
            Assert.True(createRes.StatusCode == HttpStatusCode.Created, $"Status: {createRes.StatusCode}, Body: {createBody}");
            var customerId = (await createRes.Content.ReadFromJsonAsync<ApiResponse<CustomerDto>>())!.Data!.Id;

            // Cashier searches customer -> ALLOWED (200 OK)
            var searchRes = await client.GetAsync("/api/v1/customers?searchTerm=Rohit");
            Assert.Equal(HttpStatusCode.OK, searchRes.StatusCode);

            // Cashier updates credit limit -> FORBIDDEN (403)
            var limitRes = await client.PutAsJsonAsync($"/api/v1/customers/{customerId}/credit-limit", new UpdateCreditLimitRequest(50000, "Increase limit"));
            Assert.Equal(HttpStatusCode.Forbidden, limitRes.StatusCode);

            // Cashier deactivates customer -> FORBIDDEN (403)
            var statusRes = await client.PutAsJsonAsync($"/api/v1/customers/{customerId}/status", new SetCustomerStatusRequest(false, "Deactivate customer"));
            Assert.Equal(HttpStatusCode.Forbidden, statusRes.StatusCode);

            // Manager updates credit limit -> ALLOWED (200 OK)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            var mgrLimitRes = await client.PutAsJsonAsync($"/api/v1/customers/{customerId}/credit-limit", new UpdateCreditLimitRequest(50000, "Increase limit"));
            Assert.Equal(HttpStatusCode.OK, mgrLimitRes.StatusCode);

            // Manager deactivates customer -> ALLOWED (200 OK)
            var mgrStatusRes = await client.PutAsJsonAsync($"/api/v1/customers/{customerId}/status", new SetCustomerStatusRequest(false, "Deactivate customer"));
            Assert.Equal(HttpStatusCode.OK, mgrStatusRes.StatusCode);
        }
    }

    [Fact]
    public async Task Cashier_CustomerRepayments_AllowedToRecordAndRead()
    {
        var (factory, client, _, terminalId, cashierToken, _, _) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            // Create customer
            var createRes = await client.PostAsJsonAsync("/api/v1/customers", new CreateCustomerRequest(
                "Sunil Chhetri", "9876543211", null, null, null, 20000, null));
            var customerId = (await createRes.Content.ReadFromJsonAsync<ApiResponse<CustomerDto>>())!.Data!.Id;

            // Open cash session first for terminal
            await client.PostAsJsonAsync("/api/v1/cash-sessions", new OpenCashSessionRequest(terminalId, 10000, null));

            // Record cash repayment -> ALLOWED (200 OK)
            var repayReq = new RecordCustomerPaymentRequest(5000, PaymentMethod.Cash, null, "Repay credit", Guid.NewGuid());
            var repayRes = await client.PostAsJsonAsync($"/api/v1/customers/{customerId}/payments", repayReq);
            Assert.Equal(HttpStatusCode.OK, repayRes.StatusCode);

            // Read repayments -> ALLOWED (200 OK)
            var listRes = await client.GetAsync($"/api/v1/customers/{customerId}/payments");
            Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        }
    }

    [Fact]
    public async Task CashSession_RolePermissions_PayInAllowed_PayOutAndCashDropAndForceCloseForbiddenForCashier()
    {
        var (factory, client, _, terminalId, cashierToken, managerToken, adminToken) = await SetupRolesAsync();
        using (factory)
        {
            // Cashier opens session -> ALLOWED (200 OK)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            var openRes = await client.PostAsJsonAsync("/api/v1/cash-sessions", new OpenCashSessionRequest(terminalId, 50000, "Morning shift"));
            Assert.Equal(HttpStatusCode.OK, openRes.StatusCode);
            var sessionId = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;

            // Cashier PayIn -> ALLOWED (200 OK)
            var payInRes = await client.PostAsJsonAsync($"/api/v1/cash-sessions/{sessionId}/transactions",
                new AddCashTransactionRequest(CashTransactionType.PayIn, 10000, "Float topup", Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.OK, payInRes.StatusCode);

            // Cashier PayOut -> FORBIDDEN (403)
            var payOutRes = await client.PostAsJsonAsync($"/api/v1/cash-sessions/{sessionId}/transactions",
                new AddCashTransactionRequest(CashTransactionType.PayOut, 5000, "Snacks", Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.Forbidden, payOutRes.StatusCode);

            // Cashier CashDrop -> FORBIDDEN (403)
            var dropRes = await client.PostAsJsonAsync($"/api/v1/cash-sessions/{sessionId}/transactions",
                new AddCashTransactionRequest(CashTransactionType.CashDrop, 20000, "Safe drop", Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.Forbidden, dropRes.StatusCode);

            // Cashier ForceClose -> FORBIDDEN (403)
            var forceRes = await client.PostAsJsonAsync($"/api/v1/cash-sessions/{sessionId}/force-close",
                new ForceCloseCashSessionRequest(50000, "Terminal abandoned by cashier for several days"));
            Assert.Equal(HttpStatusCode.Forbidden, forceRes.StatusCode);

            // Manager PayOut -> ALLOWED (200 OK)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            var mgrPayOutRes = await client.PostAsJsonAsync($"/api/v1/cash-sessions/{sessionId}/transactions",
                new AddCashTransactionRequest(CashTransactionType.PayOut, 5000, "Snacks", Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.OK, mgrPayOutRes.StatusCode);

            // Admin CashDrop -> ALLOWED (200 OK)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            var adminDropRes = await client.PostAsJsonAsync($"/api/v1/cash-sessions/{sessionId}/transactions",
                new AddCashTransactionRequest(CashTransactionType.CashDrop, 20000, "Safe drop", Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.OK, adminDropRes.StatusCode);

            // Manager ForceClose -> ALLOWED (200 OK)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            var mgrForceRes = await client.PostAsJsonAsync($"/api/v1/cash-sessions/{sessionId}/force-close",
                new ForceCloseCashSessionRequest(50000, "Terminal abandoned by cashier for several days"));
            Assert.Equal(HttpStatusCode.OK, mgrForceRes.StatusCode);
        }
    }
}
