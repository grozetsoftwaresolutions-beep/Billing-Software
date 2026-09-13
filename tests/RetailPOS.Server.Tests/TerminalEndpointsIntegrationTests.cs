using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class TerminalEndpointsIntegrationTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string token)> SetupOwnerAndClientAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet POS", "owner_term", "Store Owner", "OwnerPass123!", "1234"));

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "owner_term", "OwnerPass123!"));
        var loginBody = await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        var token = loginBody!.Data!.Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (factory, client, token);
    }

    [Fact]
    public async Task RegisterTerminal_NormalizesCodeToUppercase()
    {
        var (factory, client, _) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            var res = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest(
                "pos-counter-01", "Counter 1", "Receipt Printer 1"));

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>();
            Assert.NotNull(body);
            Assert.True(body.Success);
            Assert.Equal("POS-COUNTER-01", body.Data!.TerminalCode);
            Assert.Equal("Counter 1", body.Data.TerminalName);
            Assert.True(body.Data.IsActive);
        }
    }

    [Fact]
    public async Task RegisterTerminal_DuplicateCode_ReturnsTerminalAlreadyExists()
    {
        var (factory, client, _) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            var res1 = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest(
                "POS-DUP", "Counter 1"));
            Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

            // Same code in lowercase
            var res2 = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest(
                "pos-dup", "Counter 2"));
            Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);

            var body = await res2.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("TERMINAL_ALREADY_EXISTS", body.ErrorCode);
        }
    }

    [Fact]
    public async Task UpdateTerminalStatus_DeactivatesAndActivates()
    {
        var (factory, client, _) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            var regRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest(
                "POS-TOGGLE", "Counter Toggle"));
            var termId = (await regRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

            // Deactivate
            var deactRes = await client.PutAsync($"/api/v1/terminals/{termId}/status?isActive=false", null);
            Assert.Equal(HttpStatusCode.OK, deactRes.StatusCode);

            var deactBody = await deactRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>();
            Assert.NotNull(deactBody);
            Assert.False(deactBody.Data!.IsActive);

            // Re-activate
            var actRes = await client.PutAsync($"/api/v1/terminals/{termId}/status?isActive=true", null);
            Assert.Equal(HttpStatusCode.OK, actRes.StatusCode);

            var actBody = await actRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>();
            Assert.NotNull(actBody);
            Assert.True(actBody.Data!.IsActive);
        }
    }
}
