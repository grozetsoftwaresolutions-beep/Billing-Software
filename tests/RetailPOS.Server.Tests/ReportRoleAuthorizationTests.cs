using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Application.Security;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Reports;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class ReportRoleAuthorizationTests
{
    private static async Task<(
        CustomWebApplicationFactory factory,
        HttpClient client,
        string ownerToken,
        Guid terminalId,
        string cashier1Token,
        Guid cashier1Id,
        Guid cashier2Id,
        string managerToken,
        string adminToken)> SetupRolesAsync()
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

        // 3. Create Cashier 1
        var c1Res = await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "cashier_one", "Cashier One", "CashierPass123!", "1111", RoleType.Cashier));
        var cashier1Id = (await c1Res.Content.ReadFromJsonAsync<ApiResponse<UserDto>>())!.Data!.Id;

        // 4. Create Cashier 2
        var c2Res = await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "cashier_two", "Cashier Two", "CashierPass123!", "2222", RoleType.Cashier));
        var cashier2Id = (await c2Res.Content.ReadFromJsonAsync<ApiResponse<UserDto>>())!.Data!.Id;

        // 5. Create Manager
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "test_manager", "Manager User", "ManagerPass123!", "3333", RoleType.Manager));

        // 6. Create Admin
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "test_admin", "Admin User", "AdminPass123!", "4444", RoleType.Admin));

        // Login Cashier 1
        client.DefaultRequestHeaders.Authorization = null;
        var cashierLogin = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
            "cashier_one", "1111", terminalId));
        var cashier1Token = (await cashierLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // Login Manager
        var managerLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "test_manager", "ManagerPass123!"));
        var managerToken = (await managerLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // Login Admin
        var adminLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "test_admin", "AdminPass123!"));
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        return (factory, client, ownerToken, terminalId, cashier1Token, cashier1Id, cashier2Id, managerToken, adminToken);
    }

    [Fact]
    public async Task Cashier_PermittedReports_Returns200_WithCostMaskingOnStockValuation()
    {
        var (_, client, _, _, cashier1Token, _, _, _, _) = await SetupRolesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);

        // Daily sales allowed
        var dailyRes = await client.GetAsync("/api/v1/reports/daily-sales");
        Assert.Equal(HttpStatusCode.OK, dailyRes.StatusCode);

        // Current terminal allowed
        var termRes = await client.GetAsync("/api/v1/reports/current-terminal");
        Assert.Equal(HttpStatusCode.OK, termRes.StatusCode);

        // Stock valuation allowed with cost masking
        var stockRes = await client.GetAsync("/api/v1/reports/stock-valuation");
        Assert.Equal(HttpStatusCode.OK, stockRes.StatusCode);
        var stockData = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockValuationReportDto>>())!.Data!;
        Assert.Equal(0, stockData.TotalValuationAtCostPaise);
        Assert.Equal(0, stockData.PotentialProfitPaise);
        foreach (var item in stockData.Items.Items)
        {
            Assert.Equal(0, item.CostPricePaise);
            Assert.Equal(0, item.StockValuationAtCostPaise);
        }
    }

    [Fact]
    public async Task Cashier_RestrictedReports_Returns403Forbidden()
    {
        var (_, client, _, _, cashier1Token, _, _, _, _) = await SetupRolesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);

        var restrictedEndpoints = new[]
        {
            "/api/v1/reports/product-sales",
            "/api/v1/reports/purchases",
            "/api/v1/reports/supplier-balances",
            "/api/v1/reports/profit-estimate",
            "/api/v1/reports/tax-summary",
            "/api/v1/reports/customer-credit",
            "/api/v1/reports/audit-activity",
            $"/api/v1/reports/stock-ledger/{Guid.NewGuid()}"
        };

        foreach (var endpoint in restrictedEndpoints)
        {
            var res = await client.GetAsync(endpoint);
            Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"Expected 403 Forbidden for Cashier on {endpoint}, got {res.StatusCode}");
        }
    }

    [Fact]
    public async Task Cashier_SelfOnlyEnforcement_CrossQueryForbidden_OwnQueryAllowed()
    {
        var (_, client, _, _, cashier1Token, cashier1Id, cashier2Id, managerToken, _) = await SetupRolesAsync();

        // 1. Cashier querying own summary with no param -> Allowed (200)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
        var ownRes1 = await client.GetAsync("/api/v1/reports/cashier-summary");
        Assert.Equal(HttpStatusCode.OK, ownRes1.StatusCode);

        // 2. Cashier querying own summary with own ID explicitly -> Allowed (200)
        var ownRes2 = await client.GetAsync($"/api/v1/reports/cashier-summary?cashierUserId={cashier1Id}");
        Assert.Equal(HttpStatusCode.OK, ownRes2.StatusCode);

        // 3. Cashier querying another cashier's summary -> Forbidden (403)!
        var crossRes = await client.GetAsync($"/api/v1/reports/cashier-summary?cashierUserId={cashier2Id}");
        Assert.Equal(HttpStatusCode.Forbidden, crossRes.StatusCode);

        // 4. Manager querying another cashier's summary -> Allowed (200)!
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        var managerQueryRes = await client.GetAsync($"/api/v1/reports/cashier-summary?cashierUserId={cashier2Id}");
        Assert.Equal(HttpStatusCode.OK, managerQueryRes.StatusCode);
    }

    [Fact]
    public async Task ManagerAndAdminAndOwner_CanAccessAllReports()
    {
        var (_, client, ownerToken, _, _, _, _, managerToken, adminToken) = await SetupRolesAsync();

        // Manager access
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        var profitRes = await client.GetAsync("/api/v1/reports/profit-estimate");
        Assert.Equal(HttpStatusCode.OK, profitRes.StatusCode);

        var taxRes = await client.GetAsync("/api/v1/reports/tax-summary");
        Assert.Equal(HttpStatusCode.OK, taxRes.StatusCode);

        var purchaseRes = await client.GetAsync("/api/v1/reports/purchases");
        Assert.Equal(HttpStatusCode.OK, purchaseRes.StatusCode);

        // Admin access
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var auditRes = await client.GetAsync("/api/v1/reports/audit-activity");
        Assert.Equal(HttpStatusCode.OK, auditRes.StatusCode);

        // Owner access
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var creditRes = await client.GetAsync("/api/v1/reports/customer-credit");
        Assert.Equal(HttpStatusCode.OK, creditRes.StatusCode);
    }

    [Fact]
    public async Task CsvExport_RoleAuthorizationMirrored()
    {
        var (_, client, _, _, cashier1Token, _, _, managerToken, _) = await SetupRolesAsync();

        // 1. Cashier accessing restricted report CSV -> 403 Forbidden
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
        var c1ProfitCsv = await client.GetAsync("/api/v1/reports/profit-estimate/csv");
        Assert.Equal(HttpStatusCode.Forbidden, c1ProfitCsv.StatusCode);

        var c1TaxCsv = await client.GetAsync("/api/v1/reports/tax-summary/csv");
        Assert.Equal(HttpStatusCode.Forbidden, c1TaxCsv.StatusCode);

        // 2. Cashier accessing permitted report CSV -> 200 OK
        var c1DailyCsv = await client.GetAsync("/api/v1/reports/daily-sales/csv");
        Assert.Equal(HttpStatusCode.OK, c1DailyCsv.StatusCode);

        // 3. Manager accessing profit report CSV -> 200 OK
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        var mgrProfitCsv = await client.GetAsync("/api/v1/reports/profit-estimate/csv");
        Assert.Equal(HttpStatusCode.OK, mgrProfitCsv.StatusCode);
    }

    [Fact]
    public async Task CsvExport_Formats_Utf8BomAndHeaders()
    {
        var (_, client, _, _, _, _, _, managerToken, _) = await SetupRolesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        var res = await client.GetAsync("/api/v1/reports/daily-sales/csv");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var contentType = res.Content.Headers.ContentType?.ToString();
        Assert.NotNull(contentType);
        Assert.Contains("text/csv", contentType);

        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 3, "CSV response should not be empty");

        // Verify UTF-8 BOM: 0xEF, 0xBB, 0xBF
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("Metric,Value", text);
        Assert.Contains("GrossSubtotal", text);
        Assert.Contains("NetRevenue", text);
    }

    [Fact]
    public async Task CurrentTerminalReport_ReturnsOperationalSnapshot()
    {
        var (_, client, _, terminalId, cashier1Token, _, _, _, _) = await SetupRolesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
        var res = await client.GetAsync("/api/v1/reports/current-terminal");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var dto = (await res.Content.ReadFromJsonAsync<ApiResponse<CurrentTerminalReportDto>>())!.Data!;
        Assert.Equal(terminalId, dto.TerminalId);
        Assert.Equal("POS-T1", dto.TerminalCode);
        Assert.NotNull(dto.SessionStatus);
    }
// ==========================================
    // SEC-01: Daily Sales Terminal Ownership Tests
    // ==========================================

    [Fact]
    public async Task SEC01_Cashier_DailySales_OwnTerminal_Allowed200()
    {
        var (_, client, _, terminalId, cashier1Token, _, _, _, _) = await SetupRolesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);

        // 1. Without terminal param -> defaults to session terminal -> 200 OK
        var res1 = await client.GetAsync("/api/v1/reports/daily-sales");
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // 2. With own terminal param explicitly -> 200 OK
        var res2 = await client.GetAsync($"/api/v1/reports/daily-sales?terminalId={terminalId}");
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
    }

    [Fact]
    public async Task SEC01_Cashier_DailySales_DifferentTerminal_Forbidden403()
    {
        var (_, client, ownerToken, _, cashier1Token, _, _, _, _) = await SetupRolesAsync();

        // Register a second terminal
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var t2Res = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-T2", "Secondary Terminal"));
        var terminal2Id = (await t2Res.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        // Cashier 1 requests daily sales for Terminal 2 -> 403 Forbidden
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
        var res = await client.GetAsync($"/api/v1/reports/daily-sales?terminalId={terminal2Id}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("FORBIDDEN", body.ErrorCode);
    }

    [Fact]
    public async Task SEC01_Cashier_DailySales_MissingTerminalBinding_Forbidden403()
    {
        var (factory, client, _, _, _, cashier1Id, _, _, _) = await SetupRolesAsync();

        // Cashier 1 token without terminal binding generated via session manager
        var sessionManager = factory.Services.GetRequiredService<ISessionManager>();
        var cashierNoTerminalToken = sessionManager.CreateSession(
            cashier1Id, "cashier_one", "Cashier One", RoleType.Cashier, terminalId: null, TimeSpan.FromHours(8));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierNoTerminalToken);
        var res = await client.GetAsync("/api/v1/reports/daily-sales");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("TERMINAL_REQUIRED", body.ErrorCode);
    }

    [Fact]
    public async Task SEC01_ManagerAndAdminAndOwner_DailySales_StoreWideNullTerminal_Allowed200()
    {
        var (_, client, ownerToken, _, _, _, _, managerToken, adminToken) = await SetupRolesAsync();

        // Manager store-wide daily sales (no terminalId param) -> 200 OK
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        var mgrRes = await client.GetAsync("/api/v1/reports/daily-sales");
        Assert.Equal(HttpStatusCode.OK, mgrRes.StatusCode);

        // Admin store-wide daily sales -> 200 OK
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var adminRes = await client.GetAsync("/api/v1/reports/daily-sales");
        Assert.Equal(HttpStatusCode.OK, adminRes.StatusCode);

        // Owner store-wide daily sales -> 200 OK
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var ownerRes = await client.GetAsync("/api/v1/reports/daily-sales");
        Assert.Equal(HttpStatusCode.OK, ownerRes.StatusCode);
    }

    // ==========================================
    // SEC-02: Daily Sales CSV Export Ownership Tests
    // ==========================================

    [Fact]
    public async Task SEC02_Cashier_DailySalesCsv_OwnTerminal_Allowed200()
    {
        var (_, client, _, terminalId, cashier1Token, _, _, _, _) = await SetupRolesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);

        // 1. Without terminal param -> defaults to session terminal -> 200 OK with text/csv
        var res1 = await client.GetAsync("/api/v1/reports/daily-sales/csv");
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        Assert.Contains("text/csv", res1.Content.Headers.ContentType?.ToString());

        // 2. With own terminal param explicitly -> 200 OK
        var res2 = await client.GetAsync($"/api/v1/reports/daily-sales/csv?terminalId={terminalId}");
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        Assert.Contains("text/csv", res2.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task SEC02_Cashier_DailySalesCsv_DifferentTerminal_Forbidden403()
    {
        var (_, client, ownerToken, _, cashier1Token, _, _, _, _) = await SetupRolesAsync();

        // Register a second terminal
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var t2Res = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-T2", "Secondary Terminal"));
        var terminal2Id = (await t2Res.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        // Cashier 1 exports daily sales CSV for Terminal 2 -> 403 Forbidden
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
        var res = await client.GetAsync($"/api/v1/reports/daily-sales/csv?terminalId={terminal2Id}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("FORBIDDEN", body.ErrorCode);
    }

    [Fact]
    public async Task SEC02_Cashier_DailySalesCsv_MissingTerminalBinding_Forbidden403()
    {
        var (factory, client, _, _, _, cashier1Id, _, _, _) = await SetupRolesAsync();

        // Cashier 1 token without terminal binding generated via session manager
        var sessionManager = factory.Services.GetRequiredService<ISessionManager>();
        var cashierNoTerminalToken = sessionManager.CreateSession(
            cashier1Id, "cashier_one", "Cashier One", RoleType.Cashier, terminalId: null, TimeSpan.FromHours(8));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierNoTerminalToken);
        var res = await client.GetAsync("/api/v1/reports/daily-sales/csv");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("TERMINAL_REQUIRED", body.ErrorCode);
    }

    [Fact]
    public async Task SEC02_Manager_DailySalesCsv_StoreWide_Allowed200()
    {
        var (_, client, _, _, _, _, _, managerToken, _) = await SetupRolesAsync();

        // Manager store-wide daily sales CSV -> 200 OK
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        var res = await client.GetAsync("/api/v1/reports/daily-sales/csv");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("text/csv", res.Content.Headers.ContentType?.ToString());
    }
}
