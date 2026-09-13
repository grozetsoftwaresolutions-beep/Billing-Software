using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class RoleAuthorizationTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string ownerToken)> CreateFactoryWithStoreOwnerAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet POS", "auth_owner", "Store Owner", "OwnerPass123!", "1234"));

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "auth_owner", "OwnerPass123!"));
        var loginBody = await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        var token = loginBody!.Data!.Token;

        return (factory, client, token);
    }

    [Fact]
    public async Task MissingAuthorizationHeader_Returns401()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("UNAUTHENTICATED", body.ErrorCode);
    }

    [Fact]
    public async Task InvalidToken_Returns401()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid_fake_token_123");

        var response = await client.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("UNAUTHENTICATED", body.ErrorCode);
    }

    [Fact]
    public async Task CashierToken_AccessingUserManagement_Returns403Forbidden()
    {
        var (factory, client, ownerToken) = await CreateFactoryWithStoreOwnerAsync();
        using (factory)
        {
            // Owner creates cashier
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

            var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-01", "Main Counter"));
            var termId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

            await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
                "cashier_role", "Cashier User", "Pass12345!", "1122", RoleType.Cashier));

            // Cashier logs in via PIN
            client.DefaultRequestHeaders.Authorization = null;
            var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
                "cashier_role", "1122", termId));
            var cashierToken = (await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

            // Cashier attempts GET /api/v1/users
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            var usersRes = await client.GetAsync("/api/v1/users");

            Assert.Equal(HttpStatusCode.Forbidden, usersRes.StatusCode);
            var body = await usersRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("INSUFFICIENT_PERMISSIONS", body.ErrorCode);
        }
    }

    [Fact]
    public async Task Manager_AttemptingToCreateAdmin_ReturnsForbidden()
    {
        var (factory, client, ownerToken) = await CreateFactoryWithStoreOwnerAsync();
        using (factory)
        {
            // Owner creates Manager
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
                "mgr_role", "Store Manager", "Pass12345!", "3344", RoleType.Manager));

            // Manager logs in via password
            client.DefaultRequestHeaders.Authorization = null;
            var mgrLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
                "mgr_role", "Pass12345!"));
            var mgrToken = (await mgrLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

            // Manager attempts to create Admin
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mgrToken);
            var createAdminRes = await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
                "admin_attempt", "Illegal Admin", "Pass12345!", "5566", RoleType.Admin));

            Assert.Equal(HttpStatusCode.BadRequest, createAdminRes.StatusCode);
            var body = await createAdminRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("INSUFFICIENT_ROLE_PERMISSIONS", body.ErrorCode);
        }
    }

    [Fact]
    public async Task TerminalBinding_CashierAccessingDifferentTerminal_ReturnsTerminalMismatch()
    {
        var (factory, client, ownerToken) = await CreateFactoryWithStoreOwnerAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

            // Register POS-01 and POS-02
            var term1Res = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-01", "Counter 1"));
            var term1Id = (await term1Res.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

            var term2Res = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-02", "Counter 2"));
            var term2Id = (await term2Res.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

            // Create Cashier
            await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
                "bound_cashier", "Bound Cashier", "Pass12345!", "1234", RoleType.Cashier));

            // Cashier logs in on POS-01
            client.DefaultRequestHeaders.Authorization = null;
            var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
                "bound_cashier", "1234", term1Id));
            var cashierToken = (await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

            // Cashier makes request claiming X-Terminal-ID of POS-02 (term2Id)
            using var reqMismatch = new HttpRequestMessage(HttpMethod.Get, "/api/v1/terminal/validate");
            reqMismatch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            reqMismatch.Headers.Add("X-Terminal-ID", term2Id.ToString());

            var mismatchRes = await client.SendAsync(reqMismatch);
            Assert.Equal(HttpStatusCode.Forbidden, mismatchRes.StatusCode);

            var mismatchBody = await mismatchRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(mismatchBody);
            Assert.Equal("TERMINAL_MISMATCH", mismatchBody.ErrorCode);

            // Same cashier makes request on bound terminal POS-01 (term1Id) -> Allowed
            using var reqMatch = new HttpRequestMessage(HttpMethod.Get, "/api/v1/terminal/validate");
            reqMatch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            reqMatch.Headers.Add("X-Terminal-ID", term1Id.ToString());

            var matchRes = await client.SendAsync(reqMatch);
            Assert.Equal(HttpStatusCode.OK, matchRes.StatusCode);
        }
    }
}
