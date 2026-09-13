using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Server.Middleware;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class TerminalValidationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TerminalValidationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PublicHealthEndpoint_BypassesTerminalValidation()
    {
        var response = await _client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthPrefixEndpoint_BypassesTerminalValidation()
    {
        // Even if auth endpoint returns 404 (not yet mapped in Phase 3), terminal middleware must NOT block it with 403
        var response = await _client.GetAsync("/api/v1/auth/login");
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutTerminalHeader_Returns403Forbidden()
    {
        var response = await _client.GetAsync("/api/v1/terminal/validate");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(envelope);
        Assert.False(envelope.Success);
        Assert.Equal("TERMINAL_UNAUTHORIZED", envelope.ErrorCode);
        Assert.Contains("required", envelope.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithMalformedTerminalHeader_Returns403Forbidden()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/terminal/validate");
        request.Headers.Add(TerminalValidationMiddleware.HeaderName, "invalid-not-a-guid");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(envelope);
        Assert.False(envelope.Success);
        Assert.Equal("TERMINAL_UNAUTHORIZED", envelope.ErrorCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithUnregisteredTerminalGuid_Returns403Forbidden()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/terminal/validate");
        request.Headers.Add(TerminalValidationMiddleware.HeaderName, Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(envelope);
        Assert.False(envelope.Success);
        Assert.Equal("TERMINAL_UNAUTHORIZED", envelope.ErrorCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithRegisteredInactiveTerminal_Returns403Forbidden()
    {
        var terminalId = Guid.NewGuid();
        var inactiveTerminal = new Terminal(terminalId, "TERM-INACTIVE-02", "Inactive POS");
        inactiveTerminal.Deactivate();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            db.Terminals.Add(inactiveTerminal);
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/terminal/validate");
        request.Headers.Add(TerminalValidationMiddleware.HeaderName, terminalId.ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(envelope);
        Assert.False(envelope.Success);
        Assert.Equal("TERMINAL_UNAUTHORIZED", envelope.ErrorCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithRegisteredActiveTerminal_Returns200Ok()
    {
        var terminalId = Guid.NewGuid();
        var activeTerminal = new Terminal(terminalId, "TERM-ACTIVE-02", "Counter POS 2");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            db.Terminals.Add(activeTerminal);
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/terminal/validate");
        request.Headers.Add(TerminalValidationMiddleware.HeaderName, terminalId.ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
    }
}
