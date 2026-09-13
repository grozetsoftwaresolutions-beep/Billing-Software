using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetailPOS.Contracts.Common;
using RetailPOS.Infrastructure.Persistence;

namespace RetailPOS.Server.Middleware;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireTerminalAttribute : Attribute
{
}

internal static partial class TerminalValidationLoggerExtensions
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Terminal validation rejected request: {Reason} (TerminalId: {TerminalHeader}, CorrelationId: {CorrelationId})")]
    public static partial void LogTerminalRejected(
        this ILogger logger,
        string reason,
        string? terminalHeader,
        string? correlationId);
}

public sealed class TerminalValidationMiddleware
{
    public const string HeaderName = "X-Terminal-ID";
    public const string ValidatedTerminalIdItemKey = "ValidatedTerminalId";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<TerminalValidationMiddleware> _logger;

    public TerminalValidationMiddleware(RequestDelegate next, ILogger<TerminalValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RetailDbContext dbContext)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        // Bypassed paths: Health checks, system discovery, and authentication flows
        if (path.StartsWith("/api/v1/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/discovery", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/auth", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        var requiresTerminal = endpoint?.Metadata.GetMetadata<RequireTerminalAttribute>() != null;

        var hasHeader = context.Request.Headers.TryGetValue(HeaderName, out var terminalVal);

        // If endpoint does NOT require a terminal, never block the request
        if (!requiresTerminal)
        {
            if (hasHeader && Guid.TryParse(terminalVal.ToString(), out var optionalTerminalId))
            {
                var isRegistered = await dbContext.Terminals.AnyAsync(t => t.Id == optionalTerminalId && t.IsActive);
                if (isRegistered)
                {
                    context.Items[ValidatedTerminalIdItemKey] = optionalTerminalId;
                }
            }

            await _next(context);
            return;
        }

        // Endpoint strictly requires a registered terminal
        if (!hasHeader)
        {
            _logger.LogTerminalRejected("Endpoint requires registered terminal header.", null, GetCorrelationId(context));
            await RejectUnauthorizedTerminalAsync(context, "Terminal identification header (X-Terminal-ID) is required.");
            return;
        }

        var rawHeader = terminalVal.ToString();
        if (string.IsNullOrWhiteSpace(rawHeader) || !Guid.TryParse(rawHeader, out var terminalId))
        {
            _logger.LogTerminalRejected("Invalid terminal ID format.", rawHeader, GetCorrelationId(context));
            await RejectUnauthorizedTerminalAsync(context, "Invalid Terminal ID format.");
            return;
        }

        var isValid = await dbContext.Terminals.AnyAsync(t => t.Id == terminalId && t.IsActive);
        if (!isValid)
        {
            _logger.LogTerminalRejected("Supplied Terminal ID is unknown or inactive.", rawHeader, GetCorrelationId(context));
            await RejectUnauthorizedTerminalAsync(context, "Supplied Terminal ID is unknown or inactive.");
            return;
        }

        context.Items[ValidatedTerminalIdItemKey] = terminalId;
        await _next(context);
    }

    private static string? GetCorrelationId(HttpContext context)
    {
        return context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var cid) ? cid?.ToString() : null;
    }

    private static async Task RejectUnauthorizedTerminalAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
        context.Response.ContentType = "application/json";

        var correlationId = GetCorrelationId(context);
        var envelope = ApiResponse.Fail<object>(message, "TERMINAL_UNAUTHORIZED", correlationId);
        await JsonSerializer.SerializeAsync(context.Response.Body, envelope, JsonOptions);
    }
}
