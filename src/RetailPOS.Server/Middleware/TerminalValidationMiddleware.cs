using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Server.Authentication;

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
    public const string TokenHeaderName = "X-Terminal-Token";
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

    public async Task InvokeAsync(
        HttpContext context,
        RetailDbContext dbContext,
        ILocalSessionValidator sessionValidator,
        IServerIdentityService serverIdentityService)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        // 1. Fail-closed check: if server is in StartupIntegrityFailure, block business routes
        if (serverIdentityService.CurrentState == ServerState.StartupIntegrityFailure)
        {
            if (!path.StartsWith("/api/v1/health", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/api/v1/admin/recovery", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json";
                var envelope = ApiResponse.Fail<object>(
                    "Server startup integrity failure. Business operations disabled.",
                    "STARTUP_INTEGRITY_FAILURE",
                    GetCorrelationId(context));
                await JsonSerializer.SerializeAsync(context.Response.Body, envelope, JsonOptions);
                return;
            }
        }

        // 2. Whitelisted endpoints: bypass terminal validation
        if (path.StartsWith("/api/v1/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/discovery", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/auth", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/admin/recovery", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/admin/security", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/terminals/pairing", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/terminals/heartbeat", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/terminals/enroll", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/terminals/repair", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/terminals/re-pin", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }


        var endpoint = context.GetEndpoint();
        var requiresTerminal = endpoint?.Metadata.GetMetadata<RequireTerminalAttribute>() != null;
        var hasHeader = context.Request.Headers.TryGetValue(HeaderName, out var terminalVal);

        Guid? terminalId = null;
        if (hasHeader && Guid.TryParse(terminalVal.ToString(), out var parsedId))
        {
            terminalId = parsedId;
        }

        // 3. Response Precedence: Decommissioned Check (409 Conflict)
        if (terminalId.HasValue)
        {
            var terminal = await dbContext.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == terminalId.Value);
            if (terminal != null && terminal.Status == TerminalStatus.Decommissioned)
            {
                _logger.LogTerminalRejected("Terminal is permanently decommissioned.", terminalId.ToString(), GetCorrelationId(context));
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                context.Response.ContentType = "application/json";
                var envelope = ApiResponse.Fail<object>("Terminal is permanently decommissioned.", "TERMINAL_DECOMMISSIONED", GetCorrelationId(context));
                await JsonSerializer.SerializeAsync(context.Response.Body, envelope, JsonOptions);
                return;
            }
        }

        // If endpoint does NOT require a terminal, proceed
        if (!requiresTerminal)
        {
            if (terminalId.HasValue)
            {
                var isRegistered = await dbContext.Terminals.AnyAsync(t => t.Id == terminalId.Value && t.IsActive);
                if (isRegistered)
                {
                    context.Items[ValidatedTerminalIdItemKey] = terminalId.Value;
                }
            }

            await _next(context);
            return;
        }

        // Endpoint strictly requires a registered terminal
        if (!hasHeader || !terminalId.HasValue)
        {
            _logger.LogTerminalRejected("Endpoint requires registered terminal header.", null, GetCorrelationId(context));
            await RejectUnauthorizedTerminalAsync(context, "Terminal identification header (X-Terminal-ID) is required.", "TERMINAL_UNAUTHORIZED", HttpStatusCode.Forbidden);
            return;
        }

        var matchedTerminal = await dbContext.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == terminalId.Value);
        if (matchedTerminal is null || !matchedTerminal.IsActive || matchedTerminal.Status != TerminalStatus.Active)
        {
            _logger.LogTerminalRejected("Supplied Terminal ID is unknown or inactive.", terminalId.ToString(), GetCorrelationId(context));
            await RejectUnauthorizedTerminalAsync(context, "Supplied Terminal ID is unknown or inactive.", "TERMINAL_UNAUTHORIZED", HttpStatusCode.Forbidden);
            return;
        }

        // Localhost Phase 1-11 compatibility vs LAN terminal authentication
        var remoteIp = context.Connection.RemoteIpAddress;
        var isLoopback = remoteIp is null || LoopbackRecoveryMiddleware.IsLoopbackAddress(remoteIp);

        if (!isLoopback)
        {
            // Non-loopback LAN terminal: pairing token required
            if (!context.Request.Headers.TryGetValue(TokenHeaderName, out var tokenVal) ||
                string.IsNullOrWhiteSpace(tokenVal.ToString()))
            {
                _logger.LogTerminalRejected("LAN terminal requires pairing token header.", terminalId.ToString(), GetCorrelationId(context));
                await RejectUnauthorizedTerminalAsync(context, "Terminal token header (X-Terminal-Token) is required for LAN terminals.", "TERMINAL_TOKEN_REQUIRED", HttpStatusCode.Unauthorized);
                return;
            }

            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenVal.ToString()!)));
            if (!string.Equals(matchedTerminal.PairingTokenHash, tokenHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogTerminalRejected("Invalid terminal pairing token.", terminalId.ToString(), GetCorrelationId(context));
                await RejectUnauthorizedTerminalAsync(context, "Invalid terminal pairing token.", "TERMINAL_TOKEN_INVALID", HttpStatusCode.Unauthorized);
                return;
            }
        }

        // Enforce session terminal binding if session token is present
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var headerStr = authHeader.ToString();
            if (headerStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = headerStr["Bearer ".Length..].Trim();
                var session = await sessionValidator.ValidateSessionAsync(token);
                if (session.IsValid && session.TerminalId.HasValue && session.TerminalId.Value != terminalId.Value)
                {
                    _logger.LogTerminalRejected("Session terminal binding mismatch.", terminalId.ToString(), GetCorrelationId(context));
                    await RejectUnauthorizedTerminalAsync(context, "Active session is bound to a different POS terminal.", "TERMINAL_MISMATCH", HttpStatusCode.Forbidden);
                    return;
                }
            }
        }

        context.Items[ValidatedTerminalIdItemKey] = terminalId.Value;
        await _next(context);
    }

    private static string? GetCorrelationId(HttpContext context)
    {
        return context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var cid) ? cid?.ToString() : null;
    }

    private static async Task RejectUnauthorizedTerminalAsync(HttpContext context, string message, string errorCode, HttpStatusCode statusCode = HttpStatusCode.Unauthorized)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var correlationId = GetCorrelationId(context);
        var envelope = ApiResponse.Fail<object>(message, errorCode, correlationId);
        await JsonSerializer.SerializeAsync(context.Response.Body, envelope, JsonOptions);
    }
}
