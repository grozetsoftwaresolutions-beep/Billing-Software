using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Health;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Server.Configuration;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1");

        // GET /api/v1/health - Safe public server liveness and identity
        group.MapGet("/health", (HttpContext httpContext, IOptions<ServerOptions> options) =>
        {
            var config = options.Value;
            var correlationId = httpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var cid) ? cid?.ToString() : null;
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            var runtime = $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.OSDescription})";

            var response = new ServerHealthResponse(
                Status: "Healthy",
                Version: version,
                Runtime: runtime,
                ServerId: config.ServerId,
                StoreId: config.StoreId,
                StoreName: config.StoreName,
                TimestampUtc: DateTime.UtcNow);

            return Results.Ok(ApiResponse.Ok(response, correlationId));
        })
        .WithName("GetServerHealth")
        .WithSummary("Retrieves safe public server liveness and runtime metadata.");

        // GET /api/v1/health/database - Restricted to local/service diagnostics only
        group.MapGet("/health/database", async (
            HttpContext httpContext,
            RetailDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var correlationId = httpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var cid) ? cid?.ToString() : null;

            // Restrict database health diagnostics to local host callers
            var remoteIp = httpContext.Connection.RemoteIpAddress;
            var isLocal = remoteIp == null || IPAddress.IsLoopback(remoteIp);
            if (!isLocal)
            {
                return Results.Json(
                    ApiResponse.Fail<DatabaseHealthResponse>(
                        "Database health diagnostics are restricted to local service callers.",
                        "DIAGNOSTICS_FORBIDDEN",
                        correlationId),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var checkResult = await DatabaseIntegrityChecker.CheckIntegrityAsync(dbContext, cancellationToken);

            // User-safe status messages: never leak file paths, raw SQL exceptions, or stack traces
            var safeMessage = checkResult.Status == DatabaseHealthStatus.Healthy
                ? "Database integrity verified."
                : "Database integrity verification failed.";

            var dbResponse = new DatabaseHealthResponse(
                Status: checkResult.Status.ToString(),
                IsConnected: checkResult.Status != DatabaseHealthStatus.Unavailable,
                IntegrityStatus: checkResult.QuickCheckValid ? "ok" : "failed",
                ForeignKeysValid: checkResult.ForeignKeysValid,
                Message: safeMessage,
                TimestampUtc: DateTime.UtcNow);

            if (checkResult.Status == DatabaseHealthStatus.Healthy)
            {
                return Results.Ok(ApiResponse.Ok(dbResponse, correlationId));
            }

            var errorCode = $"DATABASE_{checkResult.Status.ToString().ToUpperInvariant()}";
            return Results.Json(
                ApiResponse.Fail<DatabaseHealthResponse>(safeMessage, errorCode, correlationId),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithName("GetDatabaseHealth")
        .WithSummary("Runs SQLite integrity check; restricted to local service diagnostics.");

        // GET /api/v1/terminal/validate - Validates active terminal registration
        group.MapGet("/terminal/validate", (HttpContext httpContext) =>
        {
            var correlationId = httpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var cid) ? cid?.ToString() : null;
            var terminalId = httpContext.Items.TryGetValue(TerminalValidationMiddleware.ValidatedTerminalIdItemKey, out var tid)
                ? (Guid?)tid
                : null;

            var result = new
            {
                TerminalId = terminalId,
                Validated = terminalId.HasValue,
                Message = "Terminal registration verified."
            };

            return Results.Ok(ApiResponse.Ok(result, correlationId));
        })
        .WithMetadata(new RequireTerminalAttribute())
        .WithName("ValidateTerminal")
        .WithSummary("Validates terminal registration and active status.");

        return endpoints;
    }
}
