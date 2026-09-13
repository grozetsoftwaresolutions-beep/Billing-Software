using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Common;
using RetailPOS.Server.Middleware;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class GlobalExceptionMiddlewareTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddlewareTests()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        _logger = loggerFactory.CreateLogger<GlobalExceptionMiddleware>();
    }

    [Fact]
    public async Task DomainException_WithInsufficientStock_Returns409Conflict_WithSafeEnvelope()
    {
        var correlationId = "corr-test-stock-001";
        var context = new DefaultHttpContext();
        context.Items[CorrelationIdMiddleware.ItemKey] = correlationId;
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionMiddleware(
            _ => throw new DomainException("Insufficient inventory available.", "INSUFFICIENT_STOCK"),
            _logger);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.Conflict, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var responseJson = await reader.ReadToEndAsync();

        var envelope = JsonSerializer.Deserialize<ApiResponse<object>>(responseJson, JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope.Success);
        Assert.Equal("INSUFFICIENT_STOCK", envelope.ErrorCode);
        Assert.Equal("Insufficient inventory available.", envelope.ErrorMessage);
        Assert.Equal(correlationId, envelope.CorrelationId);

        // Verify no stack trace leakage
        Assert.DoesNotContain("StackTrace", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at RetailPOS", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DomainException_GeneralValidation_Returns400BadRequest()
    {
        var correlationId = "corr-test-domain-002";
        var context = new DefaultHttpContext();
        context.Items[CorrelationIdMiddleware.ItemKey] = correlationId;
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionMiddleware(
            _ => throw new DomainException("Tax percentage cannot exceed 100%.", "INVALID_TAX_PERCENTAGE"),
            _logger);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.BadRequest, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var responseJson = await reader.ReadToEndAsync();

        var envelope = JsonSerializer.Deserialize<ApiResponse<object>>(responseJson, JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope.Success);
        Assert.Equal("INVALID_TAX_PERCENTAGE", envelope.ErrorCode);
        Assert.Equal("Tax percentage cannot exceed 100%.", envelope.ErrorMessage);
    }

    [Fact]
    public async Task UnhandledException_Returns500_MasksInternalDetails_PreservesCorrelationId()
    {
        var correlationId = "corr-fatal-500";
        var context = new DefaultHttpContext();
        context.Items[CorrelationIdMiddleware.ItemKey] = correlationId;
        context.Response.Body = new MemoryStream();

        var secretDiskPath = @"C:\Internal\Sensitive\Database\secret.db";
        var middleware = new GlobalExceptionMiddleware(
            _ => throw new InvalidOperationException($"Critical internal failure accessing {secretDiskPath}"),
            _logger);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.InternalServerError, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var responseJson = await reader.ReadToEndAsync();

        var envelope = JsonSerializer.Deserialize<ApiResponse<object>>(responseJson, JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope.Success);
        Assert.Equal("INTERNAL_SERVER_ERROR", envelope.ErrorCode);
        Assert.Equal(correlationId, envelope.CorrelationId);

        // Sensitive paths and raw error details must NOT be returned to the client
        Assert.DoesNotContain(secretDiskPath, responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Critical internal failure", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(envelope.ErrorMessage);
        Assert.Contains(correlationId, envelope.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
