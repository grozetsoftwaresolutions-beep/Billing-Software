using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Security;
using RetailPOS.Server.Authentication;

namespace RetailPOS.Server.Middleware;

public sealed class RequirePermissionFilter : IEndpointFilter
{
    private readonly Permission _permission;

    public RequirePermissionFilter(Permission permission)
    {
        _permission = permission;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var sessionValidator = httpContext.RequestServices.GetRequiredService<ILocalSessionValidator>();

        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            return Results.Json(ApiResponse.Fail<object>("Authentication required.", "UNAUTHENTICATED"), statusCode: StatusCodes.Status401Unauthorized);
        }

        var headerVal = authHeader.ToString();
        if (!headerVal.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(ApiResponse.Fail<object>("Invalid authorization scheme. Bearer token required.", "UNAUTHENTICATED"), statusCode: StatusCodes.Status401Unauthorized);
        }

        var token = headerVal["Bearer ".Length..].Trim();
        var session = await sessionValidator.ValidateSessionAsync(token);
        if (!session.IsValid || session.UserId == null || session.Role == null)
        {
            return Results.Json(ApiResponse.Fail<object>("Invalid or expired session token.", "UNAUTHENTICATED"), statusCode: StatusCodes.Status401Unauthorized);
        }

        if (session.IsLocked)
        {
            return Results.Json(ApiResponse.Fail<object>("Terminal session is locked. Please unlock to proceed.", "SESSION_LOCKED"), statusCode: StatusCodes.Status403Forbidden);
        }

        if (!RolePermissions.HasPermission(session.Role.Value, _permission))
        {
            return Results.Json(ApiResponse.Fail<object>($"You do not have the required permission: {_permission}", "INSUFFICIENT_PERMISSIONS"), statusCode: StatusCodes.Status403Forbidden);
        }

        httpContext.Items["CurrentUserId"] = session.UserId.Value;
        httpContext.Items["CurrentUsername"] = session.Username;
        httpContext.Items["CurrentUserFullName"] = session.FullName;
        httpContext.Items["CurrentUserRole"] = session.Role.Value;
        httpContext.Items["CurrentSessionTerminalId"] = session.TerminalId;
        httpContext.Items["CurrentSessionIdentityHash"] = session.SessionIdentityHash;

        return await next(context);
    }
}

public static class RequirePermissionExtensions
{
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, Permission permission)
    {
        return builder.AddEndpointFilter(new RequirePermissionFilter(permission));
    }

    public static RouteGroupBuilder RequirePermission(this RouteGroupBuilder builder, Permission permission)
    {
        return builder.AddEndpointFilter(new RequirePermissionFilter(permission));
    }
}
