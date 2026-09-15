using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Server.Middleware;

public sealed class LoopbackRecoveryMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;

    public LoopbackRecoveryMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (path.Equals("/api/v1/admin/recovery/provision-replacement-identity", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/admin/recovery/restore", StringComparison.OrdinalIgnoreCase))
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp is null || !IsLoopbackAddress(remoteIp))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";

                var failEnvelope = ApiResponse.Fail<object>(
                    "Recovery provisioning is only permitted from the local host console.",
                    "REMOTE_RECOVERY_PROVISIONING_FORBIDDEN");

                await JsonSerializer.SerializeAsync(context.Response.Body, failEnvelope, JsonOptions);
                return;
            }
        }

        await _next(context);
    }

    public static bool IsLoopbackAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return IPAddress.IsLoopback(address);
    }
}
