using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class TerminalEndpoints
{
    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/terminals");

        group.MapGet("/", async (ITerminalService terminalService, CancellationToken ct) =>
        {
            var terminals = await terminalService.GetTerminalsAsync(ct);
            return Results.Ok(ApiResponse.Ok(terminals));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetTerminals")
        .WithSummary("Lists registered POS terminals.");

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
            bool isActive,
            HttpContext context,
            ITerminalService terminalService,
            CancellationToken ct) =>
        {
            var executingUsername = (string)context.Items["CurrentUsername"]!;
            var result = await terminalService.SetTerminalStatusAsync(id, isActive, executingUsername, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("SetTerminalStatus")
        .WithSummary("Activates or deactivates a terminal counter.");

        return endpoints;
    }
}
