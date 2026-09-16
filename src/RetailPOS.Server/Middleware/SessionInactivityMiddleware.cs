using Microsoft.AspNetCore.Http;
using RetailPOS.Application.Security;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Server.Middleware;

public sealed class SessionInactivityMiddleware
{
    private readonly RequestDelegate _next;

    public SessionInactivityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ISessionManager sessionManager,
        ISecuritySettingsStore settingsStore)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Skip non-auth endpoints or unlock endpoint
        if (path.StartsWith("/api/v1/auth/setup-status", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/auth/setup-owner", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/auth/login", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/discovery", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            await _next(context);
            return;
        }

        var headerStr = authHeader.ToString();
        if (!headerStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var token = headerStr["Bearer ".Length..].Trim();
        var session = await sessionManager.ValidateSessionAsync(token, context.RequestAborted);
        if (!session.IsValid)
        {
            await _next(context);
            return;
        }

        var settings = await settingsStore.GetSettingsAsync(context.RequestAborted);
        var isUnlockPath = path.Equals("/api/v1/auth/unlock", StringComparison.OrdinalIgnoreCase);

        // EVALUATE IDLE TIMEOUT BEFORE TOUCHING ACTIVITY TIMESTAMP
        var lastActivity = session.LastActivityUtc ?? DateTime.UtcNow;
        var idleDuration = DateTime.UtcNow - lastActivity;

        if (idleDuration.TotalMinutes >= settings.IdleTimeoutMinutes)
        {
            sessionManager.LockSession(token);

            if (!isUnlockPath)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(ApiResponse.Fail<object>(
                    "Terminal session has been locked due to inactivity.", "SESSION_LOCKED"));
                return;
            }
        }
        else if (session.IsLocked)
        {
            if (!isUnlockPath)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(ApiResponse.Fail<object>(
                    "Terminal session is locked.", "SESSION_LOCKED"));
                return;
            }
        }
        else
        {
            // Active and not idle: touch session to refresh last activity
            sessionManager.TouchSession(token);
        }

        await _next(context);
    }
}
