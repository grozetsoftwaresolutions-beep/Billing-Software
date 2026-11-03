using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Application.Security;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class CashSessionRoleAuthorizationTests
{
    private static async Task<(
        CustomWebApplicationFactory factory,
        HttpClient client,
        string ownerToken,
        Guid terminal1Id,
        Guid terminal2Id,
        string cashier1Token,
        Guid cashier1Id,
        string cashier2Token,
        Guid cashier2Id,
        string cashier1NoTerminalToken,
        string managerToken,
        string adminToken)> SetupRolesAndTerminalsAsync()
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

        // 2. Register Terminal 1 and Terminal 2
        var term1Res = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-T1", "Main Terminal"));
        var terminal1Id = (await term1Res.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        var term2Res = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-T2", "Secondary Terminal"));
        var terminal2Id = (await term2Res.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

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

        // Login Cashier 1 with PIN bound to Terminal 1
        client.DefaultRequestHeaders.Authorization = null;
        var cashier1Login = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
            "cashier_one", "1111", terminal1Id));
        var cashier1Token = (await cashier1Login.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // Login Cashier 2 with PIN bound to Terminal 2
        var cashier2Login = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
            "cashier_two", "2222", terminal2Id));
        var cashier2Token = (await cashier2Login.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // Generate test Cashier 1 token WITHOUT terminal binding via session manager
        var sessionManager = factory.Services.GetRequiredService<ISessionManager>();
        var cashier1NoTerminalToken = sessionManager.CreateSession(
            cashier1Id, "cashier_one", "Cashier One", RoleType.Cashier, terminalId: null, TimeSpan.FromHours(8));

        // Login Manager
        var managerLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "test_manager", "ManagerPass123!"));
        var managerToken = (await managerLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // Login Admin
        var adminLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "test_admin", "AdminPass123!"));
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        return (factory, client, ownerToken, terminal1Id, terminal2Id,
            cashier1Token, cashier1Id, cashier2Token, cashier2Id, cashier1NoTerminalToken, managerToken, adminToken);
    }

    [Fact]
    public async Task SEC03_Cashier_OwnActiveSession_XReport_Allowed200()
    {
        var (factory, client, _, terminal1Id, _, cashier1Token, _, _, _, _, _, _) = await SetupRolesAndTerminalsAsync();
        using (factory)
        {
            // Cashier 1 opens session on Terminal 1
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
            var openRes = await client.PostAsJsonAsync("/api/v1/cash-sessions", new OpenCashSessionRequest(terminal1Id, 50000, "Morning shift"));
            Assert.Equal(HttpStatusCode.OK, openRes.StatusCode);
            var sessionId = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;

            // Cashier 1 requests X-Report for own active session -> 200 OK
            var xRes = await client.GetAsync($"/api/v1/cash-sessions/{sessionId}/x-report");
            Assert.Equal(HttpStatusCode.OK, xRes.StatusCode);

            var reportBody = await xRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionReportDto>>();
            Assert.NotNull(reportBody);
            Assert.True(reportBody.Success);
            Assert.Equal(sessionId, reportBody.Data!.Session.Id);
        }
    }

    [Fact]
    public async Task SEC03_Cashier_OwnClosedSession_ZReport_Allowed200()
    {
        var (factory, client, _, terminal1Id, _, cashier1Token, _, _, _, _, _, _) = await SetupRolesAndTerminalsAsync();
        using (factory)
        {
            // Cashier 1 opens session on Terminal 1
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
            var openRes = await client.PostAsJsonAsync("/api/v1/cash-sessions", new OpenCashSessionRequest(terminal1Id, 50000, "Shift"));
            Assert.Equal(HttpStatusCode.OK, openRes.StatusCode);
            var sessionId = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;

            // Cashier 1 closes the session
            var closeRes = await client.PostAsJsonAsync($"/api/v1/cash-sessions/{sessionId}/close", new CloseCashSessionRequest(50000, "Shift complete"));
            Assert.Equal(HttpStatusCode.OK, closeRes.StatusCode);

            // Cashier 1 requests Z-Report for own closed session -> 200 OK
            var zRes = await client.GetAsync($"/api/v1/cash-sessions/{sessionId}/z-report");
            Assert.Equal(HttpStatusCode.OK, zRes.StatusCode);

            var reportBody = await zRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionReportDto>>();
            Assert.NotNull(reportBody);
            Assert.True(reportBody.Success);
            Assert.Equal(sessionId, reportBody.Data!.Session.Id);
        }
    }

    [Fact]
    public async Task SEC03_CashierA_RequestingCashierBSession_XReport_Forbidden403()
    {
        var (factory, client, _, _, terminal2Id, cashier1Token, _, cashier2Token, _, _, _, _) = await SetupRolesAndTerminalsAsync();
        using (factory)
        {
            // Cashier 2 opens session on Terminal 2
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier2Token);
            var openRes = await client.PostAsJsonAsync("/api/v1/cash-sessions", new OpenCashSessionRequest(terminal2Id, 60000, "Cashier 2 shift"));
            Assert.Equal(HttpStatusCode.OK, openRes.StatusCode);
            var session2Id = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;

            // Cashier 1 attempts to request Cashier 2's session X-Report -> 403 Forbidden!
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
            var xRes = await client.GetAsync($"/api/v1/cash-sessions/{session2Id}/x-report");
            Assert.Equal(HttpStatusCode.Forbidden, xRes.StatusCode);

            var body = await xRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("FORBIDDEN", body.ErrorCode);
        }
    }

    [Fact]
    public async Task SEC03_CashierA_RequestingCashierBSession_ZReport_Forbidden403()
    {
        var (factory, client, _, _, terminal2Id, cashier1Token, _, cashier2Token, _, _, _, _) = await SetupRolesAndTerminalsAsync();
        using (factory)
        {
            // Cashier 2 opens session on Terminal 2
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier2Token);
            var openRes = await client.PostAsJsonAsync("/api/v1/cash-sessions", new OpenCashSessionRequest(terminal2Id, 60000, "Cashier 2 shift"));
            Assert.Equal(HttpStatusCode.OK, openRes.StatusCode);
            var session2Id = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;

            // Cashier 2 closes session
            var closeRes = await client.PostAsJsonAsync($"/api/v1/cash-sessions/{session2Id}/close", new CloseCashSessionRequest(60000, "End shift"));
            Assert.Equal(HttpStatusCode.OK, closeRes.StatusCode);

            // Cashier 1 attempts to request Cashier 2's session Z-Report -> 403 Forbidden!
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
            var zRes = await client.GetAsync($"/api/v1/cash-sessions/{session2Id}/z-report");
            Assert.Equal(HttpStatusCode.Forbidden, zRes.StatusCode);

            var body = await zRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("FORBIDDEN", body.ErrorCode);
        }
    }

    [Fact]
    public async Task SEC03_Cashier_RequestingSessionOnDifferentTerminal_Forbidden403()
    {
        var (factory, client, _, terminal1Id, terminal2Id, cashier1Token, _, _, _, _, _, _) = await SetupRolesAndTerminalsAsync();
        using (factory)
        {
            // Cashier 1 opens session on Terminal 1
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
            var openRes = await client.PostAsJsonAsync("/api/v1/cash-sessions", new OpenCashSessionRequest(terminal1Id, 50000, "T1 shift"));
            Assert.Equal(HttpStatusCode.OK, openRes.StatusCode);
            var session1Id = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;

            // Authenticate the same Cashier 1 on Terminal 2 using PIN login
            client.DefaultRequestHeaders.Authorization = null;
            var c1OnT2Login = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest("cashier_one", "1111", terminal2Id));
            Assert.Equal(HttpStatusCode.OK, c1OnT2Login.StatusCode);
            var cashier1OnT2Token = (await c1OnT2Login.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

            // Cashier 1 (now on Terminal 2) attempts to request X-Report of session created on Terminal 1 -> 403 Forbidden!
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1OnT2Token);
            var xRes = await client.GetAsync($"/api/v1/cash-sessions/{session1Id}/x-report");
            Assert.Equal(HttpStatusCode.Forbidden, xRes.StatusCode);

            var body = await xRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("FORBIDDEN", body.ErrorCode);
        }
    }

    [Fact]
    public async Task SEC03_Cashier_WithoutValidTerminalIdentity_Forbidden403()
    {
        var (factory, client, _, terminal1Id, _, cashier1Token, _, _, _, cashier1NoTerminalToken, _, _) = await SetupRolesAndTerminalsAsync();
        using (factory)
        {
            // Cashier 1 opens session on Terminal 1 using valid token
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
            var openRes = await client.PostAsJsonAsync("/api/v1/cash-sessions", new OpenCashSessionRequest(terminal1Id, 50000, "T1 shift"));
            Assert.Equal(HttpStatusCode.OK, openRes.StatusCode);
            var sessionId = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;

            // Cashier 1 attempts to request X-Report using password-only token (no terminal binding) -> 403 Forbidden!
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1NoTerminalToken);
            var xRes = await client.GetAsync($"/api/v1/cash-sessions/{sessionId}/x-report");
            Assert.Equal(HttpStatusCode.Forbidden, xRes.StatusCode);

            var body = await xRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("TERMINAL_REQUIRED", body.ErrorCode);
        }
    }

    [Fact]
    public async Task SEC03_SessionReport_NonExistentSessionId_NotFound404()
    {
        var (factory, client, _, _, _, cashier1Token, _, _, _, _, _, _) = await SetupRolesAndTerminalsAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
            var missingSessionId = Guid.NewGuid();

            var xRes = await client.GetAsync($"/api/v1/cash-sessions/{missingSessionId}/x-report");
            Assert.Equal(HttpStatusCode.NotFound, xRes.StatusCode);

            var body = await xRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("SESSION_NOT_FOUND", body.ErrorCode);
        }
    }

    [Fact]
    public async Task SEC03_Cashier_OpenSession_ZReport_BadRequest400()
    {
        var (factory, client, _, terminal1Id, _, cashier1Token, _, _, _, _, _, _) = await SetupRolesAndTerminalsAsync();
        using (factory)
        {
            // Cashier 1 opens session on Terminal 1
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
            var openRes = await client.PostAsJsonAsync("/api/v1/cash-sessions", new OpenCashSessionRequest(terminal1Id, 50000, "Open session"));
            Assert.Equal(HttpStatusCode.OK, openRes.StatusCode);
            var sessionId = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;

            // Request Z-Report while session is still OPEN -> 400 BadRequest
            var zRes = await client.GetAsync($"/api/v1/cash-sessions/{sessionId}/z-report");
            Assert.Equal(HttpStatusCode.BadRequest, zRes.StatusCode);

            var body = await zRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("SESSION_NOT_CLOSED", body.ErrorCode);
        }
    }

    [Fact]
    public async Task SEC03_ManagerAndAdminAndOwner_AnySession_XZReports_Allowed200()
    {
        var (factory, client, ownerToken, terminal1Id, _, cashier1Token, _, _, _, _, managerToken, adminToken) = await SetupRolesAndTerminalsAsync();
        using (factory)
        {
            // Cashier 1 opens and closes session on Terminal 1
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier1Token);
            var openRes = await client.PostAsJsonAsync("/api/v1/cash-sessions", new OpenCashSessionRequest(terminal1Id, 50000, "Shift"));
            Assert.Equal(HttpStatusCode.OK, openRes.StatusCode);
            var sessionId = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;

            var closeRes = await client.PostAsJsonAsync($"/api/v1/cash-sessions/{sessionId}/close", new CloseCashSessionRequest(50000, "Closing shift"));
            Assert.Equal(HttpStatusCode.OK, closeRes.StatusCode);

            // 1. Manager requests X-Report and Z-Report -> 200 OK
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            var mgrXRes = await client.GetAsync($"/api/v1/cash-sessions/{sessionId}/x-report");
            Assert.Equal(HttpStatusCode.OK, mgrXRes.StatusCode);

            var mgrZRes = await client.GetAsync($"/api/v1/cash-sessions/{sessionId}/z-report");
            Assert.Equal(HttpStatusCode.OK, mgrZRes.StatusCode);

            // 2. Admin requests X-Report and Z-Report -> 200 OK
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            var adminXRes = await client.GetAsync($"/api/v1/cash-sessions/{sessionId}/x-report");
            Assert.Equal(HttpStatusCode.OK, adminXRes.StatusCode);

            var adminZRes = await client.GetAsync($"/api/v1/cash-sessions/{sessionId}/z-report");
            Assert.Equal(HttpStatusCode.OK, adminZRes.StatusCode);

            // 3. Owner requests X-Report and Z-Report -> 200 OK
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            var ownerXRes = await client.GetAsync($"/api/v1/cash-sessions/{sessionId}/x-report");
            Assert.Equal(HttpStatusCode.OK, ownerXRes.StatusCode);

            var ownerZRes = await client.GetAsync($"/api/v1/cash-sessions/{sessionId}/z-report");
            Assert.Equal(HttpStatusCode.OK, ownerZRes.StatusCode);
        }
    }
}
