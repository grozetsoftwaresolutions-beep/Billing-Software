using System.Net;
using System.Net.Http.Json;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Health;
using RetailPOS.Server.Middleware;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class HealthEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointsTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetServerHealth_Returns200_WithMetadataAndCorrelationId()
    {
        var response = await _client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));

        var headerCorrelationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).First();

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ServerHealthResponse>>();
        Assert.NotNull(body);
        Assert.True(body.Success);
        Assert.NotNull(body.Data);
        Assert.Equal("Healthy", body.Data.Status);
        Assert.Equal("STORE-TEST-001", body.Data.StoreId);
        Assert.Equal("Grozet Test Store", body.Data.StoreName);
        Assert.Equal("SRV-TEST-01", body.Data.ServerId);
        Assert.Equal(headerCorrelationId, body.CorrelationId);
    }

    [Fact]
    public async Task GetServerHealth_FromRemoteClient_Returns200_ForSafeLiveness()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Add("X-Test-Remote-IP", "192.168.1.105");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ServerHealthResponse>>();
        Assert.NotNull(body);
        Assert.True(body.Success);
    }

    [Fact]
    public async Task GetDatabaseHealth_FromLocalCaller_Returns200_WithSanitizedOutput()
    {
        var response = await _client.GetAsync("/api/v1/health/database");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));

        var rawJson = await response.Content.ReadAsStringAsync();

        // Must never expose database file paths or internal disk details
        Assert.DoesNotContain(".db", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRAGMA", rawJson, StringComparison.OrdinalIgnoreCase);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<DatabaseHealthResponse>>();
        Assert.NotNull(body);
        Assert.True(body.Success);
        Assert.NotNull(body.Data);
        Assert.True(body.Data.IsConnected);
        Assert.Equal("Healthy", body.Data.Status);
        Assert.Equal("ok", body.Data.IntegrityStatus);
        Assert.True(body.Data.ForeignKeysValid);
        Assert.Equal("Database integrity verified.", body.Data.Message);
    }

    [Fact]
    public async Task GetDatabaseHealth_FromRemoteCaller_Returns403Forbidden()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health/database");
        request.Headers.Add("X-Test-Remote-IP", "192.168.1.200");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<DatabaseHealthResponse>>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.Equal("DIAGNOSTICS_FORBIDDEN", body.ErrorCode);
        Assert.Contains("restricted to local", body.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
