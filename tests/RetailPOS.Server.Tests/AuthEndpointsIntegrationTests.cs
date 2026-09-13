using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Persistence;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class AuthEndpointsIntegrationTests
{
    [Fact]
    public async Task GetSetupStatus_WhenFresh_ReturnsSetupRequired()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/setup-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<SetupStatusResponse>>();
        Assert.NotNull(body);
        Assert.True(body.Success);
        Assert.True(body.Data!.IsSetupRequired);
    }

    [Fact]
    public async Task SetupOwner_WithValidData_Succeeds_AndSubsequentStatusIsFalse()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var request = new SetupOwnerRequest(
            StoreName: "Grozet Main",
            Username: "storeowner",
            FullName: "Primary Owner",
            Password: "OwnerPassword123!",
            Pin: "1234");

        var response = await client.PostAsJsonAsync("/api/v1/auth/setup-owner", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var statusRes = await client.GetAsync("/api/v1/auth/setup-status");
        var statusBody = await statusRes.Content.ReadFromJsonAsync<ApiResponse<SetupStatusResponse>>();
        Assert.NotNull(statusBody);
        Assert.False(statusBody.Data!.IsSetupRequired);
        Assert.Equal("Grozet Main", statusBody.Data.StoreName);
    }

    [Fact]
    public async Task SetupOwner_SequentialSecondCall_FailsWithSetupAlreadyCompleted()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var request1 = new SetupOwnerRequest(
            StoreName: "Grozet Store 1",
            Username: "owner1",
            FullName: "Owner One",
            Password: "Password123!",
            Pin: "1234");

        var response1 = await client.PostAsJsonAsync("/api/v1/auth/setup-owner", request1);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

        var request2 = new SetupOwnerRequest(
            StoreName: "Grozet Store 2",
            Username: "owner2",
            FullName: "Owner Two",
            Password: "Password123!",
            Pin: "5678");

        var response2 = await client.PostAsJsonAsync("/api/v1/auth/setup-owner", request2);
        Assert.Equal(HttpStatusCode.BadRequest, response2.StatusCode);

        var body2 = await response2.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body2);
        Assert.False(body2.Success);
        Assert.Equal("SETUP_ALREADY_COMPLETED", body2.ErrorCode);
    }

    [Fact]
    public async Task SetupOwner_ConcurrentRaceCondition_ExactlyOneSucceeds_AndOthersFailSafely()
    {
        using var factory = new CustomWebApplicationFactory();

        // 5 concurrent requests attempting to setup owner at the exact same moment
        var tasks = Enumerable.Range(1, 5).Select(async i =>
        {
            var client = factory.CreateClient();
            var req = new SetupOwnerRequest(
                StoreName: $"Grozet Store {i}",
                Username: $"concurrent_owner_{i}",
                FullName: $"Concurrent Owner {i}",
                Password: "ComplexPassword123!",
                Pin: $"123{i}");

            return await client.PostAsJsonAsync("/api/v1/auth/setup-owner", req);
        }).ToList();

        var responses = await Task.WhenAll(tasks);

        var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var failureCount = responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest);

        Assert.Equal(1, successCount);
        Assert.Equal(4, failureCount);

        // Verify database state: exactly 1 owner account and exactly 1 store record
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();

        var owners = await db.Users.Where(u => u.Role == RoleType.Owner).ToListAsync();
        Assert.Single(owners);

        var stores = await db.Stores.ToListAsync();
        Assert.Single(stores);
    }

    [Fact]
    public async Task LoginWithPassword_OwnerCredentials_Succeeds()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet POS", "owner_test", "Store Owner", "Pass12345!", "1234"));

        var loginReq = new LoginPasswordRequest("owner_test", "Pass12345!");
        var response = await client.PostAsJsonAsync("/api/v1/auth/login-password", loginReq);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        Assert.NotNull(body);
        Assert.True(body.Success);
        Assert.NotNull(body.Data!.Token);
        Assert.Equal(RoleType.Owner, body.Data.Role);
    }

    [Fact]
    public async Task LoginWithPassword_PreservesPasswordWhitespace_DoesNotTrim()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var exactPassword = "  MySecretPassword123!  ";
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet POS", "owner_ws", "Store Owner", exactPassword, "1234"));

        // Trimmed password must fail
        var trimmedReq = new LoginPasswordRequest("owner_ws", exactPassword.Trim());
        var trimmedRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", trimmedReq);
        Assert.Equal(HttpStatusCode.Unauthorized, trimmedRes.StatusCode);

        // Exact password with whitespace must succeed
        var exactReq = new LoginPasswordRequest("owner_ws", exactPassword);
        var exactRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", exactReq);
        Assert.Equal(HttpStatusCode.OK, exactRes.StatusCode);
    }

    [Fact]
    public async Task LoginWithPassword_CashierAttemptingPasswordLogin_ReturnsGenericAuthFailed()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // 1. Setup Owner
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet POS", "owner_setup", "Owner", "Pass12345!", "1234"));

        // 2. Login Owner to get token
        var ownerLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest("owner_setup", "Pass12345!"));
        var ownerBody = await ownerLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        var ownerToken = ownerBody!.Data!.Token;

        // 3. Create Cashier
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "cashier_p1", "Cashier User", "CashierPass123!", "4321", RoleType.Cashier));

        client.DefaultRequestHeaders.Authorization = null;

        // 4. Cashier attempting password login must be rejected
        var cashierLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "cashier_p1", "CashierPass123!"));

        Assert.Equal(HttpStatusCode.Unauthorized, cashierLogin.StatusCode);
        var resBody = await cashierLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        Assert.NotNull(resBody);
        Assert.Equal("AUTH_FAILED", resBody.ErrorCode);
    }

    [Fact]
    public async Task LoginWithPin_CashierOnActiveTerminal_Succeeds()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // Setup owner and login
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet POS", "owner_pin", "Owner", "Pass12345!", "1234"));
        var ownerLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest("owner_pin", "Pass12345!"));
        var ownerToken = (await ownerLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // Register terminal POS-01
        var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-01", "Main Counter"));
        var termBody = await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>();
        var terminalId = termBody!.Data!.Id;

        // Create cashier
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "cashier_pin", "Cashier Jane", "CashierPass123!", "8899", RoleType.Cashier));

        client.DefaultRequestHeaders.Authorization = null;

        // Cashier PIN login with valid terminal
        var pinReq = new LoginPinRequest("cashier_pin", "8899", terminalId);
        var pinRes = await client.PostAsJsonAsync("/api/v1/auth/login-pin", pinReq);

        Assert.Equal(HttpStatusCode.OK, pinRes.StatusCode);
        var pinBody = await pinRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        Assert.NotNull(pinBody);
        Assert.True(pinBody.Success);
        Assert.NotNull(pinBody.Data!.Token);
        Assert.Equal(terminalId, pinBody.Data.TerminalId);
    }

    [Fact]
    public async Task LoginWithPin_ManagerOrAdminOrOwner_Rejected()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // Setup Owner
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet POS", "owner_nop", "Owner", "Pass12345!", "1234"));
        var ownerLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest("owner_nop", "Pass12345!"));
        var ownerToken = (await ownerLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-02", "Counter 2"));
        var terminalId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        // Create Manager
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "mgr_test", "Manager Bob", "ManagerPass123!", "9999", RoleType.Manager));

        client.DefaultRequestHeaders.Authorization = null;

        // Owner attempting PIN login must be rejected
        var ownerPinRes = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest("owner_nop", "1234", terminalId));
        Assert.Equal(HttpStatusCode.Unauthorized, ownerPinRes.StatusCode);

        // Manager attempting PIN login must be rejected
        var mgrPinRes = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest("mgr_test", "9999", terminalId));
        Assert.Equal(HttpStatusCode.Unauthorized, mgrPinRes.StatusCode);
    }

    [Fact]
    public async Task LoginWithPin_FiveConsecutiveFailures_PersistsLockout()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // Setup owner and register terminal
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet POS", "owner_lock", "Owner", "Pass12345!", "1234"));
        var ownerLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest("owner_lock", "Pass12345!"));
        var ownerToken = (await ownerLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-03", "Counter 3"));
        var terminalId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "cashier_lock", "Cashier Lockout", "Pass12345!", "5555", RoleType.Cashier));

        client.DefaultRequestHeaders.Authorization = null;

        // Perform 5 failed attempts
        for (var i = 1; i <= 5; i++)
        {
            var failRes = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest("cashier_lock", "0000", terminalId));
            Assert.Equal(HttpStatusCode.Unauthorized, failRes.StatusCode);
        }

        // Even with correct PIN, the account is locked out
        var lockedRes = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest("cashier_lock", "5555", terminalId));
        Assert.Equal(HttpStatusCode.Unauthorized, lockedRes.StatusCode);

        // Verify lockout state persisted in database across requests
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
        var user = await db.Users.FirstAsync(u => u.Username == "cashier_lock");
        Assert.Equal(5, user.FailedPinAttempts);
        Assert.NotNull(user.LockoutEndUtc);
        Assert.True(user.LockoutEndUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task SessionLifecycle_ValidateAndLogout_RevokesToken()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet POS", "owner_sess", "Owner", "Pass12345!", "1234"));

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest("owner_sess", "Pass12345!"));
        var token = (await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // Validate session with Bearer header
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var sessionRes = await client.GetAsync("/api/v1/auth/session");
        Assert.Equal(HttpStatusCode.OK, sessionRes.StatusCode);

        var sessionBody = await sessionRes.Content.ReadFromJsonAsync<ApiResponse<SessionStatusResponse>>();
        Assert.NotNull(sessionBody);
        Assert.True(sessionBody.Data!.IsValid);
        Assert.Equal("owner_sess", sessionBody.Data.Username);

        // Logout
        var logoutRes = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logoutRes.StatusCode);

        // Session must now be invalid
        var postLogoutRes = await client.GetAsync("/api/v1/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, postLogoutRes.StatusCode);
    }
}
