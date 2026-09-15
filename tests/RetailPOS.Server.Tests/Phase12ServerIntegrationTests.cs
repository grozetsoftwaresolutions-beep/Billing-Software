namespace RetailPOS.Server.Tests;

using System;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Server.Middleware;
using Xunit;

public sealed class Phase12ServerIntegrationTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Phase12ServerIntegrationTests()
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

    private async Task<(string ownerToken, string adminToken, string managerToken, string cashierToken)> SetupUsersAndTokensAsync()
    {
        // 1. Owner
        var setupRes = await _client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Retail POS 12", "phase12_owner", "Store Owner", "OwnerPass123!", "1234"));
        var setupBody = await setupRes.Content.ReadAsStringAsync();
        Assert.True(setupRes.IsSuccessStatusCode, $"SetupOwner failed: {setupRes.StatusCode}, {setupBody}");

        var loginOwner = await _client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "phase12_owner", "OwnerPass123!"));
        var ownerToken = (await loginOwner.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // Register initial terminal for PIN logins
        var initTermRes = await _client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-INIT", "Init Terminal"));
        var initTermId = (await initTermRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        // 2. Admin
        await _client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "phase12_admin", "Admin User", "AdminPass123!", "2222", RoleType.Admin));
        var loginAdmin = await _client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "phase12_admin", "AdminPass123!"));
        var adminToken = (await loginAdmin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // 3. Manager
        await _client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "phase12_manager", "Manager User", "ManagerPass123!", "3333", RoleType.Manager));
        var loginMgr = await _client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "phase12_manager", "ManagerPass123!"));
        var managerToken = (await loginMgr.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // 4. Cashier (logs in via PIN)
        await _client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "phase12_cashier", "Cashier User", "CashierPass123!", "4444", RoleType.Cashier));
        _client.DefaultRequestHeaders.Authorization = null;
        var loginCashier = await _client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
            "phase12_cashier", "4444", initTermId));
        var cashierToken = (await loginCashier.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        return (ownerToken, adminToken, managerToken, cashierToken);
    }

    [Fact]
    public async Task RoleAuthorization_CashierCannotGenerateEnrollmentToken_Returns403Forbidden()
    {
        var (_, _, _, cashierToken) = await SetupUsersAndTokensAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/terminals/tokens/enrollment");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
        request.Content = JsonContent.Create(new EnrollmentTokenRequestDto(EnrollmentTokenPurpose.NewEnrollment));

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("INSUFFICIENT_PERMISSIONS", body.ErrorCode);
    }

    [Fact]
    public async Task RoleAuthorization_ManagerCanGenerateEnrollmentToken_Returns200()
    {
        var (_, _, managerToken, _) = await SetupUsersAndTokensAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/terminals/tokens/enrollment");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        request.Content = JsonContent.Create(new EnrollmentTokenRequestDto(EnrollmentTokenPurpose.NewEnrollment));

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<EnrollmentTokenResponseDto>>();
        Assert.NotNull(body);
        Assert.NotNull(body.Data);
        Assert.NotEmpty(body.Data.Token);
        Assert.Equal(EnrollmentTokenPurpose.NewEnrollment, body.Data.Purpose);
    }

    [Fact]
    public async Task RoleAuthorization_ManagerCannotGenerateRePinningToken_Returns403Forbidden()
    {
        var (ownerToken, _, managerToken, _) = await SetupUsersAndTokensAsync();

        // Register a terminal first
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var termRes = await _client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-M1", "Counter 1"));
        var terminalId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        // Manager attempts re-pinning token generation
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/terminals/tokens/re-pinning");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        request.Content = JsonContent.Create(new RePinningTokenRequestDto(terminalId));

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("INSUFFICIENT_PERMISSIONS", body.ErrorCode);
    }

    [Fact]
    public async Task RoleAuthorization_AdminAndOwnerCanGenerateRePinningToken_Returns200()
    {
        var (ownerToken, adminToken, _, _) = await SetupUsersAndTokensAsync();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var termRes = await _client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-AO1", "Counter AO"));
        var terminalId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        // Owner generates re-pinning token
        var ownerReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/terminals/tokens/re-pinning");
        ownerReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        ownerReq.Content = JsonContent.Create(new RePinningTokenRequestDto(terminalId));

        var ownerRes = await _client.SendAsync(ownerReq);
        var errContent = await ownerRes.Content.ReadAsStringAsync(); Assert.True(ownerRes.IsSuccessStatusCode, $"Status: {ownerRes.StatusCode}, Body: {errContent}");
        var ownerBody = await ownerRes.Content.ReadFromJsonAsync<ApiResponse<RePinningTokenResponseDto>>();
        Assert.NotNull(ownerBody?.Data?.Token);

        // Admin generates re-pinning token
        var adminReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/terminals/tokens/re-pinning");
        adminReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        adminReq.Content = JsonContent.Create(new RePinningTokenRequestDto(terminalId));

        var adminRes = await _client.SendAsync(adminReq);
        Assert.Equal(HttpStatusCode.OK, adminRes.StatusCode);
        var adminBody = await adminRes.Content.ReadFromJsonAsync<ApiResponse<RePinningTokenResponseDto>>();
        Assert.NotNull(adminBody?.Data?.Token);
    }

    [Fact]
    public async Task DecommissionedTerminal_Returns409Conflict_PrecedenceOverAuthAndRole()
    {
        var (ownerToken, _, _, _) = await SetupUsersAndTokensAsync();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var termRes = await _client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-DEC1", "Counter Decom"));
        var terminalId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        // Decommission the terminal
        var decomRes = await _client.PostAsync($"/api/v1/terminals/{terminalId}/decommission", null);
        Assert.Equal(HttpStatusCode.OK, decomRes.StatusCode);

        // Send request to a [RequireTerminal] endpoint with X-Terminal-ID and NO auth header
        var testReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hardware/printer/test-print");
        testReq.Headers.Add("X-Terminal-ID", terminalId.ToString());
        testReq.Content = JsonContent.Create(new TestPrintRequestDto("Test Decommission Header"));

        var testRes = await _client.SendAsync(testReq);

        // Precedence rule: Decommission check (409 Conflict) happens BEFORE 401 unauthenticated check
        Assert.Equal(HttpStatusCode.Conflict, testRes.StatusCode);

        var body = await testRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("TERMINAL_DECOMMISSIONED", body.ErrorCode);
    }

    [Fact]
    public async Task LanTerminal_WithoutTokenHeader_Returns401_TerminalTokenRequired()
    {
        var (ownerToken, _, _, _) = await SetupUsersAndTokensAsync();

        // Enroll a terminal to get ID and pairing token
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var tokenRes = await _client.PostAsJsonAsync("/api/v1/terminals/tokens/enrollment", new EnrollmentTokenRequestDto(EnrollmentTokenPurpose.NewEnrollment));
        var tokenData = (await tokenRes.Content.ReadFromJsonAsync<ApiResponse<EnrollmentTokenResponseDto>>())!.Data!;

        var enrollRes = await _client.PostAsJsonAsync("/api/v1/terminals/enroll", new TerminalEnrollRequestDto(
            tokenData.Token, "POS-LAN1", "LAN Counter 1", "LAN-PC-01"));
        var enrolled = (await enrollRes.Content.ReadFromJsonAsync<ApiResponse<TerminalEnrollResponseDto>>())!.Data!;

        // Make LAN request (remote IP = 192.168.1.180) to [RequireTerminal] endpoint without X-Terminal-Token
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hardware/printer/test-print");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        req.Headers.Add("X-Terminal-ID", enrolled.TerminalId.ToString());
        req.Headers.Add("X-Test-Remote-IP", "192.168.1.180");
        req.Content = JsonContent.Create(new TestPrintRequestDto("LAN Print"));

        var response = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("TERMINAL_TOKEN_REQUIRED", body.ErrorCode);
    }

    [Fact]
    public async Task LanTerminal_WithInvalidTokenHeader_Returns401_TerminalTokenInvalid()
    {
        var (ownerToken, _, _, _) = await SetupUsersAndTokensAsync();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var tokenRes = await _client.PostAsJsonAsync("/api/v1/terminals/tokens/enrollment", new EnrollmentTokenRequestDto(EnrollmentTokenPurpose.NewEnrollment));
        var tokenData = (await tokenRes.Content.ReadFromJsonAsync<ApiResponse<EnrollmentTokenResponseDto>>())!.Data!;

        var enrollRes = await _client.PostAsJsonAsync("/api/v1/terminals/enroll", new TerminalEnrollRequestDto(
            tokenData.Token, "POS-LAN2", "LAN Counter 2", "LAN-PC-02"));
        var enrolled = (await enrollRes.Content.ReadFromJsonAsync<ApiResponse<TerminalEnrollResponseDto>>())!.Data!;

        // Make LAN request with invalid pairing token
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hardware/printer/test-print");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        req.Headers.Add("X-Terminal-ID", enrolled.TerminalId.ToString());
        req.Headers.Add("X-Terminal-Token", "completely_wrong_token_value_xyz");
        req.Headers.Add("X-Test-Remote-IP", "192.168.1.180");
        req.Content = JsonContent.Create(new TestPrintRequestDto("LAN Print"));

        var response = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("TERMINAL_TOKEN_INVALID", body.ErrorCode);
    }

    [Fact]
    public async Task LanTerminal_WithValidTokenHeader_Succeeds()
    {
        var (ownerToken, _, _, _) = await SetupUsersAndTokensAsync();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var tokenRes = await _client.PostAsJsonAsync("/api/v1/terminals/tokens/enrollment", new EnrollmentTokenRequestDto(EnrollmentTokenPurpose.NewEnrollment));
        var tokenData = (await tokenRes.Content.ReadFromJsonAsync<ApiResponse<EnrollmentTokenResponseDto>>())!.Data!;

        var enrollRes = await _client.PostAsJsonAsync("/api/v1/terminals/enroll", new TerminalEnrollRequestDto(
            tokenData.Token, "POS-LAN3", "LAN Counter 3", "LAN-PC-03"));
        var enrolled = (await enrollRes.Content.ReadFromJsonAsync<ApiResponse<TerminalEnrollResponseDto>>())!.Data!;

        // Make LAN request with correct pairing token
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hardware/printer/test-print");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        req.Headers.Add("X-Terminal-ID", enrolled.TerminalId.ToString());
        req.Headers.Add("X-Terminal-Token", enrolled.TerminalAuthToken);
        req.Headers.Add("X-Test-Remote-IP", "192.168.1.180");
        req.Content = JsonContent.Create(new TestPrintRequestDto("LAN Print"));

        var response = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LocalhostTerminal_BypassesTokenHeaderRequirement()
    {
        var (ownerToken, _, _, _) = await SetupUsersAndTokensAsync();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var termRes = await _client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-LOCAL1", "Localhost Counter"));
        var terminalId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        // Loopback request (no remote IP header -> TestServer loopback) without X-Terminal-Token
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hardware/printer/test-print");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        req.Headers.Add("X-Terminal-ID", terminalId.ToString());
        req.Content = JsonContent.Create(new TestPrintRequestDto("Local Print"));

        var response = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TerminalHeartbeat_UpdatesLastHeartbeatAndOnlineStatus()
    {
        var (ownerToken, _, _, _) = await SetupUsersAndTokensAsync();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var termRes = await _client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-HB1", "Heartbeat Counter"));
        var terminalId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        var heartbeatDto = new TerminalHeartbeatDto(
            terminalId,
            "POS-HB-PC",
            "192.168.1.205",
            DateTimeOffset.UtcNow);

        var response = await _client.PostAsJsonAsync("/api/v1/terminals/heartbeat", heartbeatDto);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TerminalHeartbeatResponseDto>();
        Assert.NotNull(body);
        Assert.Equal(terminalId, body.TerminalId);
        Assert.Equal(TerminalStatus.Active, body.Status);
        Assert.Equal(CounterConnectivityStatus.Online, body.Connectivity);

        // Verify database persistence
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
        var terminal = await db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == terminalId);
        Assert.NotNull(terminal);
        Assert.NotNull(terminal.LastHeartbeatUtc);
        Assert.True(DateTime.UtcNow - terminal.LastHeartbeatUtc.Value < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task EndToEnd_TerminalEnrollment_SingleUseToken_RepairFlow_Decommission()
    {
        var (ownerToken, _, _, _) = await SetupUsersAndTokensAsync();

        // 1. Owner generates enrollment token
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var tokenRes = await _client.PostAsJsonAsync("/api/v1/terminals/tokens/enrollment", new EnrollmentTokenRequestDto(EnrollmentTokenPurpose.NewEnrollment));
        var tokenData = (await tokenRes.Content.ReadFromJsonAsync<ApiResponse<EnrollmentTokenResponseDto>>())!.Data!;

        // 2. Anonymous client enrolls using single-use token
        _client.DefaultRequestHeaders.Authorization = null;
        var enrollRes = await _client.PostAsJsonAsync("/api/v1/terminals/enroll", new TerminalEnrollRequestDto(
            tokenData.Token, "POS-E2E", "Counter E2E", "E2E-PC-01"));
        Assert.Equal(HttpStatusCode.OK, enrollRes.StatusCode);
        var enrolled = (await enrollRes.Content.ReadFromJsonAsync<ApiResponse<TerminalEnrollResponseDto>>())!.Data!;
        Assert.NotNull(enrolled);
        Assert.NotEmpty(enrolled.TerminalAuthToken);

        // 3. Second attempt with the SAME enrollment token must be rejected (single-use)
        var secondEnrollRes = await _client.PostAsJsonAsync("/api/v1/terminals/enroll", new TerminalEnrollRequestDto(
            tokenData.Token, "POS-E2E-FAIL", "Counter Fail", "E2E-PC-02"));
        Assert.False(secondEnrollRes.IsSuccessStatusCode);

        // 4. Counter executes authenticated LAN operation using initial pairing token
        var lanReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hardware/printer/test-print");
        lanReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        lanReq.Headers.Add("X-Terminal-ID", enrolled.TerminalId.ToString());
        lanReq.Headers.Add("X-Terminal-Token", enrolled.TerminalAuthToken);
        lanReq.Headers.Add("X-Test-Remote-IP", "192.168.1.180");
        lanReq.Content = JsonContent.Create(new TestPrintRequestDto("E2E Initial Print"));

        var lanRes = await _client.SendAsync(lanReq);
        Assert.Equal(HttpStatusCode.OK, lanRes.StatusCode);

        // 5. Owner generates repair token for this specific terminal
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var repairTokenRes = await _client.PostAsJsonAsync("/api/v1/terminals/tokens/repair", new EnrollmentTokenRequestDto(
            EnrollmentTokenPurpose.ExistingTerminalRepair, enrolled.TerminalId));
        var repairTokenData = (await repairTokenRes.Content.ReadFromJsonAsync<ApiResponse<EnrollmentTokenResponseDto>>())!.Data!;

        // 6. Counter repairs its pairing
        _client.DefaultRequestHeaders.Authorization = null;
        var repairRes = await _client.PostAsJsonAsync("/api/v1/terminals/repair", new TerminalRepairRequestDto(
            enrolled.TerminalId, repairTokenData.Token));
        Assert.Equal(HttpStatusCode.OK, repairRes.StatusCode);
        var repaired = (await repairRes.Content.ReadFromJsonAsync<ApiResponse<TerminalEnrollResponseDto>>())!.Data!;
        Assert.NotEmpty(repaired.TerminalAuthToken);

        // 7. LAN request with NEW pairing token succeeds
        var newLanReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hardware/printer/test-print");
        newLanReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        newLanReq.Headers.Add("X-Terminal-ID", enrolled.TerminalId.ToString());
        newLanReq.Headers.Add("X-Terminal-Token", repaired.TerminalAuthToken);
        newLanReq.Headers.Add("X-Test-Remote-IP", "192.168.1.180");
        newLanReq.Content = JsonContent.Create(new TestPrintRequestDto("E2E Repaired Print"));

        var newLanRes = await _client.SendAsync(newLanReq);
        Assert.Equal(HttpStatusCode.OK, newLanRes.StatusCode);

        // 8. LAN request with OLD pairing token is now rejected (invalid)
        var oldLanReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hardware/printer/test-print");
        oldLanReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        oldLanReq.Headers.Add("X-Terminal-ID", enrolled.TerminalId.ToString());
        oldLanReq.Headers.Add("X-Terminal-Token", enrolled.TerminalAuthToken);
        oldLanReq.Headers.Add("X-Test-Remote-IP", "192.168.1.180");
        oldLanReq.Content = JsonContent.Create(new TestPrintRequestDto("E2E Old Token Print"));

        var oldLanRes = await _client.SendAsync(oldLanReq);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLanRes.StatusCode);

        // 9. Owner decommissions terminal permanently
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var decomRes = await _client.PostAsync($"/api/v1/terminals/{enrolled.TerminalId}/decommission", null);
        Assert.Equal(HttpStatusCode.OK, decomRes.StatusCode);

        // 10. Subsequent requests from decommissioned terminal return 409 Conflict
        var afterDecomReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hardware/printer/test-print");
        afterDecomReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        afterDecomReq.Headers.Add("X-Terminal-ID", enrolled.TerminalId.ToString());
        afterDecomReq.Headers.Add("X-Terminal-Token", repaired.TerminalAuthToken);
        afterDecomReq.Headers.Add("X-Test-Remote-IP", "192.168.1.180");
        afterDecomReq.Content = JsonContent.Create(new TestPrintRequestDto("E2E Post Decom Print"));

        var afterDecomRes = await _client.SendAsync(afterDecomReq);
        Assert.Equal(HttpStatusCode.Conflict, afterDecomRes.StatusCode);
    }
}
