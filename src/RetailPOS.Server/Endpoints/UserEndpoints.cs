using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/users")
            .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner);

        group.MapGet("/", async (IUserService userService, CancellationToken ct) =>
        {
            var users = await userService.GetUsersAsync(ct);
            return Results.Ok(ApiResponse.Ok(users));
        })
        .WithName("GetUsers")
        .WithSummary("Retrieves all store users.");

        group.MapPost("/", async (
            CreateUserRequest request,
            HttpContext context,
            IUserService userService,
            CancellationToken ct) =>
        {
            var executingUserId = (Guid)context.Items["CurrentUserId"]!;
            var executingRole = (RoleType)context.Items["CurrentUserRole"]!;
            var executingUsername = (string)context.Items["CurrentUsername"]!;

            var result = await userService.CreateUserAsync(request, executingUserId, executingRole, executingUsername, ct);
            return Results.Ok(result);
        })
        .WithName("CreateUser")
        .WithSummary("Creates a new cashier or staff account enforcing role hierarchy.");

        group.MapPut("/{id:guid}/deactivate", async (
            Guid id,
            HttpContext context,
            IUserService userService,
            CancellationToken ct) =>
        {
            var executingUserId = (Guid)context.Items["CurrentUserId"]!;
            var executingRole = (RoleType)context.Items["CurrentUserRole"]!;
            var executingUsername = (string)context.Items["CurrentUsername"]!;

            var result = await userService.DeactivateUserAsync(id, executingUserId, executingRole, executingUsername, ct);
            return Results.Ok(result);
        })
        .WithName("DeactivateUser")
        .WithSummary("Deactivates an account enforcing role hierarchy.");

        group.MapPut("/{id:guid}/activate", async (
            Guid id,
            HttpContext context,
            IUserService userService,
            CancellationToken ct) =>
        {
            var executingUserId = (Guid)context.Items["CurrentUserId"]!;
            var executingRole = (RoleType)context.Items["CurrentUserRole"]!;
            var executingUsername = (string)context.Items["CurrentUsername"]!;

            var result = await userService.ActivateUserAsync(id, executingUserId, executingRole, executingUsername, ct);
            return Results.Ok(result);
        })
        .WithName("ActivateUser")
        .WithSummary("Activates an account enforcing role hierarchy.");

        return endpoints;
    }
}
