using System.Net;
using System.Net.Http.Json;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Discovery;
using RetailPOS.Contracts.Terminals;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class MultiCounterVersionCompatibilityTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MultiCounterVersionCompatibilityTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task MissingProtocolHeader_Returns400BadRequest()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ProtocolVersionConstants.ClientProtocolVersionHeader);
        client.DefaultRequestHeaders.Add("X-Terminal-ID", Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/v1/products");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PROTOCOL_HEADER_MISSING", body);
    }

    [Fact]
    public async Task DeprecatedProtocol_Returns426UpgradeRequired()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ProtocolVersionConstants.ClientProtocolVersionHeader);
        client.DefaultRequestHeaders.Add(ProtocolVersionConstants.ClientProtocolVersionHeader, "0");
        client.DefaultRequestHeaders.Add("X-Terminal-ID", Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/v1/products");

        Assert.Equal((HttpStatusCode)426, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CLIENT_PROTOCOL_DEPRECATED", body);
    }

    [Fact]
    public async Task FutureProtocol_Returns409Conflict()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ProtocolVersionConstants.ClientProtocolVersionHeader);
        client.DefaultRequestHeaders.Add(ProtocolVersionConstants.ClientProtocolVersionHeader, "999");
        client.DefaultRequestHeaders.Add("X-Terminal-ID", Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/v1/products");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CLIENT_PROTOCOL_FUTURE", body);
    }

    [Fact]
    public async Task DynamicDiscovery_ReturnsProtocolAndApplicationVersions()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/discovery/server-info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<ServerDiscoveryResponseDto>>();
        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        var discovery = envelope.Data;
        Assert.NotNull(discovery);
        Assert.Equal(ProtocolVersionConstants.CurrentProtocolVersion, discovery.ProtocolVersion);
        Assert.Equal(ProtocolVersionConstants.MinSupportedClientProtocolVersion, discovery.MinSupportedClientProtocolVersion);
        Assert.False(string.IsNullOrWhiteSpace(discovery.ServerVersion));
    }
}
