using System.Net;
using System.Net.Http.Json;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Health;
using RetailPOS.Server.Middleware;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class CorrelationIdTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CorrelationIdTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ClientSuppliedCorrelationId_PropagatedToResponseHeaderAndEnvelope()
    {
        var customId = "TERMINAL-01-REQ-99999";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, customId);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));
        Assert.Equal(customId, response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).First());

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ServerHealthResponse>>();
        Assert.NotNull(body);
        Assert.Equal(customId, body.CorrelationId);
    }

    [Fact]
    public async Task OmittedCorrelationId_GeneratesUniqueNewCorrelationId()
    {
        var response1 = await _client.GetAsync("/api/v1/health");
        var response2 = await _client.GetAsync("/api/v1/health");

        var id1 = response1.Headers.GetValues(CorrelationIdMiddleware.HeaderName).First();
        var id2 = response2.Headers.GetValues(CorrelationIdMiddleware.HeaderName).First();

        Assert.NotEmpty(id1);
        Assert.NotEmpty(id2);
        Assert.NotEqual(id1, id2);

        var body1 = await response1.Content.ReadFromJsonAsync<ApiResponse<ServerHealthResponse>>();
        var body2 = await response2.Content.ReadFromJsonAsync<ApiResponse<ServerHealthResponse>>();

        Assert.NotNull(body1);
        Assert.NotNull(body2);
        Assert.Equal(id1, body1.CorrelationId);
        Assert.Equal(id2, body2.CorrelationId);
    }
}
