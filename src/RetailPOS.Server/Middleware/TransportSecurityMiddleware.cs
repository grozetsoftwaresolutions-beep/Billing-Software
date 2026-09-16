using System.Net;
using Microsoft.AspNetCore.Http;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Server.Middleware;

public sealed class TransportSecurityMiddleware
{
    private readonly RequestDelegate _next;

    public TransportSecurityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Check if request touches supervisor approval or credentials
        var isSupervisorOperation =
            path.StartsWith("/api/v1/auth/supervisor-approve", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Headers.ContainsKey("X-Supervisor-Approval-Token") ||
            context.Request.Headers.ContainsKey("X-Supervisor-Token") ||
            context.Request.Headers.ContainsKey("X-Supervisor-Pin");

        if (isSupervisorOperation)
        {
            var isHttps = context.Request.IsHttps ||
                          string.Equals(context.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);

            var remoteIp = context.Connection.RemoteIpAddress;
            var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
            IPAddress? ipToCheck = remoteIp;
            if (!string.IsNullOrWhiteSpace(forwardedFor))
            {
                var firstIp = forwardedFor.Split(',')[0].Trim();
                if (IPAddress.TryParse(firstIp, out var parsedFwd))
                {
                    ipToCheck = parsedFwd;
                }
            }

            var isLoopback = ipToCheck == null ||
                             IPAddress.IsLoopback(ipToCheck) ||
                             ipToCheck.ToString() == "127.0.0.1" ||
                             ipToCheck.ToString() == "::1";

            // Require HTTPS or Loopback for supervisor credentials/tokens over remote LAN
            if (!isHttps && !isLoopback)
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                context.Response.Headers["Upgrade"] = "TLS/1.3, HTTP/2.0";
                await context.Response.WriteAsJsonAsync(ApiResponse.Fail<object>(
                    "Supervisor approval tokens and credentials require HTTPS/TLS over remote LAN.",
                    "UPGRADE_REQUIRED"));
                return;
            }
        }

        await _next(context);
    }
}
