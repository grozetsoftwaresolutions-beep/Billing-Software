namespace RetailPOS.Server.Endpoints;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Diagnostics;
using RetailPOS.Domain.Common;

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/diagnostics");

        // POST /api/v1/admin/diagnostics/generate - Generate sanitized support bundle
        group.MapPost("/generate", async (
            GenerateDiagnosticsRequestDto? request,
            ISupportDiagnosticsService diagnosticsService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = httpContext.User.FindFirst("sub")?.Value ?? "SYSTEM";
            var userName = httpContext.User.Identity?.Name ?? "Administrator";

            try
            {
                var record = await diagnosticsService.GenerateSupportBundleAsync(
                    userId,
                    userName,
                    request?.Reason,
                    ct).ConfigureAwait(false);

                return Results.Created($"/api/v1/admin/diagnostics/{record.OperationId}", ApiResponse.Ok(record, "Diagnostic bundle created successfully."));
            }
            catch (DomainException ex) when (ex.ErrorCode == "OPERATION_ALREADY_IN_PROGRESS")
            {
                return Results.Conflict(ApiResponse.Fail<DiagnosticBundleMetadataDto>(ex.Message, "OPERATION_IN_PROGRESS"));
            }
            catch (DomainException ex) when (ex.ErrorCode == "SECRET_LEAK_DETECTED")
            {
                return Results.UnprocessableEntity(ApiResponse.Fail<DiagnosticBundleMetadataDto>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<DiagnosticBundleMetadataDto>(ex.Message, ex.ErrorCode));
            }
        });

        // GET /api/v1/admin/diagnostics - List diagnostic bundles
        group.MapGet("", async (ISupportDiagnosticsService diagnosticsService, CancellationToken ct) =>
        {
            var history = await diagnosticsService.GetBundleHistoryAsync(ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok(history));
        });

        // GET /api/v1/admin/diagnostics/{operationId:guid}/download - Download diagnostic bundle ZIP
        group.MapGet("/{operationId:guid}/download", async (
            Guid operationId,
            ISupportDiagnosticsService diagnosticsService,
            CancellationToken ct) =>
        {
            var stream = await diagnosticsService.OpenBundleReadStreamAsync(operationId, ct).ConfigureAwait(false);
            if (stream == null)
            {
                return Results.NotFound(ApiResponse.Fail<object>("Diagnostic bundle file not found.", "NOT_FOUND"));
            }

            var fileName = $"retailpos_support_{operationId:N}.zip";
            return Results.File(stream, "application/zip", fileName);
        });

        // GET /api/v1/admin/diagnostics/environment - Get system environment snapshot
        group.MapGet("/environment", async (ISupportDiagnosticsService diagnosticsService, CancellationToken ct) =>
        {
            var env = await diagnosticsService.GetSystemEnvironmentAsync(ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok(env));
        });

        // GET /api/v1/admin/diagnostics/health-summary - Get system health summary
        group.MapGet("/health-summary", async (ISupportDiagnosticsService diagnosticsService, CancellationToken ct) =>
        {
            var health = await diagnosticsService.GetServerHealthSummaryAsync(ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok(health));
        });

        return app;
    }
}
