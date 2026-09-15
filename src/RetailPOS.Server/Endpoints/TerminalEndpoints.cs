using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class TerminalEndpoints
{
    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/terminals");

        // Token Generation Endpoints (Role Protected)
        group.MapPost("/tokens/enrollment", async (
            EnrollmentTokenRequestDto request,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? string.Empty;
            var result = await enrollmentService.GenerateEnrollmentTokenAsync(request, currentUserId, ct);
            return Results.Ok(ApiResponse.Ok(result));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GenerateEnrollmentToken")
        .WithSummary("Generates an enrollment token for a new terminal.");

        group.MapPost("/tokens/repair", async (
            EnrollmentTokenRequestDto request,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? string.Empty;
            var repairRequest = request with { Purpose = EnrollmentTokenPurpose.ExistingTerminalRepair };
            var result = await enrollmentService.GenerateEnrollmentTokenAsync(repairRequest, currentUserId, ct);
            return Results.Ok(ApiResponse.Ok(result));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GenerateRepairToken")
        .WithSummary("Generates a repair token for an existing terminal.");

        group.MapPost("/tokens/re-pinning", async (
            RePinningTokenRequestDto request,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? string.Empty;
            var result = await enrollmentService.GenerateRePinningTokenAsync(request, currentUserId, ct);
            return Results.Ok(ApiResponse.Ok(result));
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("GenerateRePinningToken")
        .WithSummary("Generates a re-pinning token for disaster recovery server replacement.");

        // Anonymous Terminal Lifecycle Operations
        group.MapPost("/enroll", async (
            TerminalEnrollRequestDto request,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            try
            {
                var clientIp = context.Connection.RemoteIpAddress?.ToString();
                var enrollRequest = request with { ClientIpAddress = clientIp ?? request.ClientIpAddress };
                var result = await enrollmentService.EnrollTerminalAsync(enrollRequest, ct);
                return Results.Ok(ApiResponse.Ok(result));
            }
            catch (DomainException ex) when (ex.ErrorCode == "TERMINAL_DECOMMISSIONED")
            {
                return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .WithName("EnrollTerminal")
        .WithSummary("Enrolls a new POS terminal using a single-use token.");

        group.MapPost("/repair", async (
            TerminalRepairRequestDto request,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            try
            {
                var clientIp = context.Connection.RemoteIpAddress?.ToString();
                var result = await enrollmentService.RepairTerminalAsync(request.TerminalId, request.RepairToken, clientIp ?? request.ClientIpAddress, ct);
                return Results.Ok(ApiResponse.Ok(result));
            }
            catch (DomainException ex) when (ex.ErrorCode == "TERMINAL_DECOMMISSIONED")
            {
                return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .WithName("RepairTerminal")
        .WithSummary("Repairs an existing terminal with a repair token.");

        group.MapPost("/re-pin", async (
            RePinningRequestDto request,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            try
            {
                var result = await enrollmentService.RePinTerminalAsync(request, ct);
                return Results.Ok(result);
            }
            catch (DomainException ex) when (ex.ErrorCode == "TERMINAL_DECOMMISSIONED")
            {
                return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .WithName("RePinTerminal")
        .WithSummary("Re-pins server identity post-disaster recovery.");

        group.MapPost("/heartbeat", async (
            TerminalHeartbeatDto request,
            HttpContext context,
            ITerminalHeartbeatTracker heartbeatTracker,
            CancellationToken ct) =>
        {
            var clientIp = context.Connection.RemoteIpAddress?.ToString();
            var response = await heartbeatTracker.RecordHeartbeatAsync(request with { ClientIpAddress = clientIp }, ct);
            return Results.Ok(response);
        })
        .WithName("TerminalHeartbeat")
        .WithSummary("Records periodic terminal heartbeat.");

        // Terminal Query and Management (Role Protected)
        group.MapGet("/", async (
            ITerminalService terminalService,
            CancellationToken ct) =>
        {
            var terminals = await terminalService.GetTerminalsAsync(ct);
            return Results.Ok(ApiResponse.Ok(terminals));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetTerminals")
        .WithSummary("Lists registered POS terminals.");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            var detail = await enrollmentService.GetTerminalDetailAsync(id, ct);
            if (detail is null)
                return Results.NotFound(ApiResponse.Fail<object>("Terminal not found.", "TERMINAL_NOT_FOUND"));

            return Results.Ok(ApiResponse.Ok(detail));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetTerminalById")
        .WithSummary("Gets details of a specific POS terminal.");

        group.MapPost("/", async (
            RegisterTerminalRequest request,
            HttpContext context,
            ITerminalService terminalService,
            CancellationToken ct) =>
        {
            var executingUsername = (string)context.Items["CurrentUsername"]!;
            var result = await terminalService.RegisterTerminalAsync(request, executingUsername, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("RegisterTerminal")
        .WithSummary("Registers a new POS terminal counter.");

        group.MapPut("/{id:guid}/status", async (
            Guid id,
            bool? isActive,
            TerminalStatusUpdateDto? request,
            HttpContext context,
            ITerminalService terminalService,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            var executingUsername = (string)context.Items["CurrentUsername"]!;
            var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? string.Empty;

            if (isActive.HasValue)
            {
                var result = await terminalService.SetTerminalStatusAsync(id, isActive.Value, executingUsername, ct);
                return Results.Ok(result);
            }

            if (request != null)
            {
                try
                {
                    await enrollmentService.UpdateTerminalStatusAsync(id, request.Status, currentUserId, ct);
                    return Results.Ok(ApiResponse.Ok(new { TerminalId = id, request.Status }, "Terminal status updated."));
                }
                catch (DomainException ex) when (ex.ErrorCode == "TERMINAL_DECOMMISSIONED" || ex.ErrorCode == "TERMINAL_ALREADY_DECOMMISSIONED")
                {
                    return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
                }
                catch (DomainException ex)
                {
                    return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
                }
            }

            return Results.BadRequest(ApiResponse.Fail<object>("Status parameter is required.", "INVALID_REQUEST"));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("SetTerminalStatus")
        .WithSummary("Updates terminal operational status.");

        group.MapPost("/{id:guid}/decommission", async (
            Guid id,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            try
            {
                var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? string.Empty;
                await enrollmentService.DecommissionTerminalAsync(id, currentUserId, ct);
                return Results.Ok(ApiResponse.Ok(new { TerminalId = id, Status = TerminalStatus.Decommissioned }, "Terminal permanently decommissioned."));
            }
            catch (DomainException ex) when (ex.ErrorCode == "TERMINAL_ALREADY_DECOMMISSIONED")
            {
                return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("DecommissionTerminal")
        .WithSummary("Permanently decommissions a POS terminal.");

        return endpoints;
    }
}
