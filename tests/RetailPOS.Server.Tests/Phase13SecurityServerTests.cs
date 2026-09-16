using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Application.Security;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Sales;
using RetailPOS.Contracts.Security;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class Phase13SecurityServerTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Phase13SecurityServerTests()
    {
        _factory = new CustomWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(string ownerToken, Guid terminalId)> SetupOwnerAndTerminalAsync(string prefix)
    {
        var username = $"{prefix}_owner";
        await _client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet Test POS", username, "Test Owner", "OwnerPass123!", "1234"));

        var loginRes = await _client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            username, "OwnerPass123!"));
        var loginEnvelope = await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        var token = loginEnvelope!.Data!.Token;

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var termRes = await _client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest($"{prefix}-T1", "Main Register"));
        var termEnvelope = await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>();
        var terminalId = termEnvelope!.Data!.Id;

        return (token, terminalId);
    }

    private async Task<(string token, Guid userId)> CreateAndLoginUserAsync(
        string ownerToken, string username, string pin, RoleType role, Guid terminalId)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var createRes = await _client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            username, $"{username} User", "UserPass123!", pin, role));
        var userEnvelope = await createRes.Content.ReadFromJsonAsync<ApiResponse<UserDto>>();
        var userId = userEnvelope!.Data!.Id;

        _client.DefaultRequestHeaders.Authorization = null;
        HttpResponseMessage loginRes;
        if (role == RoleType.Cashier)
        {
            loginRes = await _client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
                username, pin, terminalId));
        }
        else
        {
            loginRes = await _client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
                username, "UserPass123!"));
        }

        var loginEnvelope = await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        var token = loginEnvelope?.Data?.Token ?? throw new InvalidOperationException($"Login failed for {username}: {loginEnvelope?.ErrorMessage}");

        return (token, userId);
    }

    [Fact]
    public async Task TransportSecurity_RemoteLanHttp_SupervisorApprove_Returns426UpgradeRequired()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/supervisor-approve");
        request.Headers.Add("X-Forwarded-For", "192.168.1.150");
        request.Content = JsonContent.Create(new SupervisorApprovalRequest(
            "admin", "1234", null, Permission.VoidSale, "SaleInvoice", Guid.NewGuid().ToString(), "nonce-1", "Test Reason"));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        Assert.True(response.Headers.Contains("Upgrade"));
        Assert.Contains("TLS/1.3", response.Headers.GetValues("Upgrade").First());
    }

    [Fact]
    public async Task TransportSecurity_RemoteLanHttp_WithApprovalTokenHeader_Returns426()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sales/arbitrary-id");
        request.Headers.Add("X-Forwarded-For", "192.168.1.150");
        request.Headers.Add("X-Supervisor-Approval-Token", "sup-token-12345");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task TransportSecurity_LoopbackHttp_PassesThroughMiddleware()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/supervisor-approve");
        request.Content = JsonContent.Create(new SupervisorApprovalRequest(
            "admin", "1234", null, Permission.VoidSale, "SaleInvoice", Guid.NewGuid().ToString(), "nonce-2", "Test Reason"));

        var response = await _client.SendAsync(request);

        // Does not return 426
        Assert.NotEqual(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task TransportSecurity_RemoteLanHttps_WithForwardedProto_PassesThrough()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/supervisor-approve");
        request.Headers.Add("X-Forwarded-For", "192.168.1.150");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Content = JsonContent.Create(new SupervisorApprovalRequest(
            "admin", "1234", null, Permission.VoidSale, "SaleInvoice", Guid.NewGuid().ToString(), "nonce-3", "Test Reason"));

        var response = await _client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task TerminalLock_And_Unlock_Flow_Success()
    {
        var (ownerToken, terminalId) = await SetupOwnerAndTerminalAsync("lockflow");
        var (cashierToken, _) = await CreateAndLoginUserAsync(ownerToken, "cashier_lock", "3344", RoleType.Cashier, terminalId);

        // Explicit lock
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
        var lockRes = await _client.PostAsync("/api/v1/auth/lock", null);
        Assert.Equal(HttpStatusCode.OK, lockRes.StatusCode);

        // Calling protected endpoint while locked returns 403 SESSION_LOCKED
        var testRes = await _client.GetAsync("/api/v1/audit");
        Assert.Equal(HttpStatusCode.Forbidden, testRes.StatusCode);
        var failBody = await testRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("SESSION_LOCKED", failBody?.ErrorCode);

        // Unlock with invalid PIN fails
        var invalidUnlockRes = await _client.PostAsJsonAsync("/api/v1/auth/unlock", new TerminalUnlockRequest("0000"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidUnlockRes.StatusCode);

        // Unlock with correct PIN succeeds
        var validUnlockRes = await _client.PostAsJsonAsync("/api/v1/auth/unlock", new TerminalUnlockRequest("3344"));
        Assert.Equal(HttpStatusCode.OK, validUnlockRes.StatusCode);
    }

    [Fact]
    public async Task SupervisorApproval_IssueAndConsumeForVoidSale_Succeeds()
    {
        var (ownerToken, terminalId) = await SetupOwnerAndTerminalAsync("supvoid");
        var (managerToken, _) = await CreateAndLoginUserAsync(ownerToken, "sup_mgr", "8899", RoleType.Manager, terminalId);
        var (cashierToken, cashierId) = await CreateAndLoginUserAsync(ownerToken, "sup_cashier", "1122", RoleType.Cashier, terminalId);

        // Create a completed sale directly in db
        var saleId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            var invoice = new SaleInvoice(
                saleId, "INV-SEC-001", Guid.NewGuid(), terminalId, cashierId,
                PricingModel.TaxInclusive, Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(),
                Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(), Money.FromMinorUnits(5000));

            invoice.AddPayment(new PaymentRecord(
                Guid.NewGuid(), saleId, PaymentMethod.Cash, Money.FromMinorUnits(5000), Money.FromMinorUnits(5000), Money.Zero()));

            db.SalesInvoices.Add(invoice);
            await db.SaveChangesAsync();
        }

        var nonce = "nonce-" + Guid.NewGuid().ToString("N");

        // 1. Issue supervisor approval token
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
        var approvalRes = await _client.PostAsJsonAsync("/api/v1/auth/supervisor-approve", new SupervisorApprovalRequest(
            "sup_mgr", "8899", null, Permission.VoidSale, "SaleInvoice", saleId.ToString(), nonce, "Voiding sale"));

        Assert.Equal(HttpStatusCode.OK, approvalRes.StatusCode);
        var approvalEnvelope = await approvalRes.Content.ReadFromJsonAsync<ApiResponse<SupervisorApprovalResponse>>();
        Assert.NotNull(approvalEnvelope?.Data?.ApprovalToken);
        var approvalToken = approvalEnvelope.Data.ApprovalToken;

        // 2. Cashier consumes approval token to void sale
        var voidRes = await _client.PostAsJsonAsync($"/api/v1/sales/{saleId}/void", new VoidSaleRequest(
            "Customer cancelled", approvalToken, nonce));

        Assert.Equal(HttpStatusCode.OK, voidRes.StatusCode);
        var voidEnvelope = await voidRes.Content.ReadFromJsonAsync<ApiResponse<VoidSaleResponse>>();
        Assert.True(voidEnvelope?.Success);
        Assert.Equal(saleId, voidEnvelope?.Data?.SaleId);

        // 3. Replay attack with same token must fail
        var replayRes = await _client.PostAsJsonAsync($"/api/v1/sales/{saleId}/void", new VoidSaleRequest(
            "Customer cancelled duplicate", approvalToken, nonce));

        Assert.False(replayRes.IsSuccessStatusCode);
    }

    [Fact]
    public async Task RoleChange_StrictlyRestrictedToOwner()
    {
        var (ownerToken, terminalId) = await SetupOwnerAndTerminalAsync("roleauth");
        var (adminToken, _) = await CreateAndLoginUserAsync(ownerToken, "admin_user", "5555", RoleType.Admin, terminalId);
        var (cashierToken, cashierUserId) = await CreateAndLoginUserAsync(ownerToken, "target_cashier", "6666", RoleType.Cashier, terminalId);

        // Admin attempts to change role -> Forbidden 403
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var adminChangeRes = await _client.PutAsJsonAsync($"/api/v1/users/{cashierUserId}/role", new UpdateUserRoleRequest(RoleType.Manager));
        Assert.Equal(HttpStatusCode.Forbidden, adminChangeRes.StatusCode);

        // Owner changes role -> OK 200
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var ownerChangeRes = await _client.PutAsJsonAsync($"/api/v1/users/{cashierUserId}/role", new UpdateUserRoleRequest(RoleType.Manager));
        Assert.Equal(HttpStatusCode.OK, ownerChangeRes.StatusCode);
    }

    [Fact]
    public async Task ResetUserCredentials_RevokesExistingUserSessionsImmediately()
    {
        var (ownerToken, terminalId) = await SetupOwnerAndTerminalAsync("credreset");
        var (cashierToken, cashierUserId) = await CreateAndLoginUserAsync(ownerToken, "cashier_revoked", "7777", RoleType.Cashier, terminalId);

        // Verify cashier token is valid
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
        var checkRes1 = await _client.GetAsync("/api/v1/categories");
        Assert.NotEqual(HttpStatusCode.Unauthorized, checkRes1.StatusCode);

        // Owner resets credentials
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var resetRes = await _client.PutAsJsonAsync($"/api/v1/users/{cashierUserId}/credentials", new ResetUserCredentialsRequest("BrandNewPass123!", "9988"));
        Assert.Equal(HttpStatusCode.OK, resetRes.StatusCode);

        // Existing cashier session token must now be invalid
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
        var checkRes2 = await _client.GetAsync("/api/v1/categories");
        Assert.Equal(HttpStatusCode.Unauthorized, checkRes2.StatusCode);
    }

    [Fact]
    public async Task SecuritySettings_And_AuditEndpoints_AuthorizationEnforced()
    {
        var (ownerToken, terminalId) = await SetupOwnerAndTerminalAsync("secset");
        var (cashierToken, _) = await CreateAndLoginUserAsync(ownerToken, "cashier_sec", "2345", RoleType.Cashier, terminalId);

        // Cashier cannot view audit logs
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
        var auditResCashier = await _client.GetAsync("/api/v1/audit");
        Assert.Equal(HttpStatusCode.Forbidden, auditResCashier.StatusCode);

        // Owner can view audit logs
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var auditResOwner = await _client.GetAsync("/api/v1/audit");
        Assert.Equal(HttpStatusCode.OK, auditResOwner.StatusCode);

        // Owner can export audit CSV
        var exportResOwner = await _client.GetAsync("/api/v1/audit/export");
        Assert.Equal(HttpStatusCode.OK, exportResOwner.StatusCode);
        Assert.Equal("text/csv", exportResOwner.Content.Headers.ContentType?.MediaType);

        // Cashier cannot update security settings
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
        var updateSecCashier = await _client.PutAsJsonAsync("/api/v1/settings/security", new UpdateSecuritySettingsRequest(
            30, 5, 15, 10m, true, true, true));
        Assert.Equal(HttpStatusCode.Forbidden, updateSecCashier.StatusCode);

        // Owner can update security settings
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var updateSecOwner = await _client.PutAsJsonAsync("/api/v1/settings/security", new UpdateSecuritySettingsRequest(
            20, 4, 10, 15m, true, true, true));
        var content = await updateSecOwner.Content.ReadAsStringAsync();
        Assert.True(updateSecOwner.IsSuccessStatusCode, "Response: " + content);
    }
}
