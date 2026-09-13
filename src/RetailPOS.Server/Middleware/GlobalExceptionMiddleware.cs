using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Common;

namespace RetailPOS.Server.Middleware;

internal static partial class GlobalExceptionLoggerExtensions
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Domain rule violation [{ErrorCode}]: {ErrorMessage} (CorrelationId: {CorrelationId})")]
    public static partial void LogDomainRuleViolation(
        this ILogger logger,
        string errorCode,
        string errorMessage,
        string? correlationId,
        Exception? exception);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Unhandled server error (CorrelationId: {CorrelationId})")]
    public static partial void LogUnhandledServerError(
        this ILogger logger,
        string? correlationId,
        Exception? exception);
}

public sealed class GlobalExceptionMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var cid) ? cid?.ToString() : null;

        context.Response.ContentType = "application/json";

        if (exception is DomainException domainEx)
        {
            var statusCode = domainEx.ErrorCode == "INSUFFICIENT_STOCK"
                ? HttpStatusCode.Conflict
                : HttpStatusCode.BadRequest;

            context.Response.StatusCode = (int)statusCode;
            _logger.LogDomainRuleViolation(domainEx.ErrorCode, domainEx.Message, correlationId, domainEx);

            var envelope = ApiResponse.Fail<object>(domainEx.Message, domainEx.ErrorCode, correlationId);
            await JsonSerializer.SerializeAsync(context.Response.Body, envelope, JsonOptions);
        }
        else
        {
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            _logger.LogUnhandledServerError(correlationId, exception);

            var userSafeMessage = $"An unexpected server error occurred. Please contact support referencing correlation ID: {correlationId}";
            var envelope = ApiResponse.Fail<object>(userSafeMessage, "INTERNAL_SERVER_ERROR", correlationId);
            await JsonSerializer.SerializeAsync(context.Response.Body, envelope, JsonOptions);
        }
    }
}
