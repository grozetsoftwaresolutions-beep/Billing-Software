using System.Net;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Security;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class BootstrapTrustBoundaryTests
{
    [Fact]
    public async Task SetupOwner_FromLoopback_SucceedsAndReturnsRecoveryKey()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

        var request = new SetupOwnerRequest(
            StoreName: "Grozet Boundary Store",
            Username: "boundowner",
            FullName: "Primary Owner",
            Password: "OwnerPassword123!",
            Pin: "1234");

        var response = await client.PostAsJsonAsync("/api/v1/auth/setup-owner", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<SetupOwnerResponse>>();
        Assert.NotNull(body);
        Assert.True(body.Success);
        Assert.NotNull(body.Data);
        Assert.NotEqual(Guid.Empty, body.Data.StoreGuid);
        Assert.Equal(64, body.Data.StoreMasterRecoveryKey.Length);
    }

    [Fact]
    public async Task SetupOwner_FromLan_Returns403Forbidden()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "192.168.1.100");

        var request = new SetupOwnerRequest(
            StoreName: "Grozet Boundary Store",
            Username: "boundowner",
            FullName: "Primary Owner",
            Password: "OwnerPassword123!",
            Pin: "1234");

        var response = await client.PostAsJsonAsync("/api/v1/auth/setup-owner", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SecurityEndpoints_FromLan_Returns403Forbidden()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "192.168.1.150");

        var response = await client.GetAsync("/api/v1/admin/security/rotation-status");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SecurityEndpoints_FromLoopback_AllowsAccess()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

        var response = await client.GetAsync("/api/v1/admin/security/rotation-status");

        // Allowed through loopback middleware (returns 200 or 401 if unauthenticated, but NOT 403 Forbidden loopback block)
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
