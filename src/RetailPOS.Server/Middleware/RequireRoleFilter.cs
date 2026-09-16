using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Authentication;

namespace RetailPOS.Server.Middleware;

public sealed class RequireRoleFilter : IEndpointFilter
{
    private readonly RoleType[] _allowedRoles;

    public RequireRoleFilter(params RoleType[] allowedRoles)
    {
        _allowedRoles = allowedRoles;
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

        if (_allowedRoles.Length > 0 && !_allowedRoles.Contains(session.Role.Value))
        {
            return Results.Json(ApiResponse.Fail<object>("You do not have permission to access this resource.", "INSUFFICIENT_PERMISSIONS"), statusCode: StatusCodes.Status403Forbidden);
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

public static class RequireRoleExtensions
{
    public static RouteHandlerBuilder RequireRole(this RouteHandlerBuilder builder, params RoleType[] roles)
    {
        return builder.AddEndpointFilter(new RequireRoleFilter(roles));
    }

    public static RouteGroupBuilder RequireRole(this RouteGroupBuilder builder, params RoleType[] roles)
    {
        return builder.AddEndpointFilter(new RequireRoleFilter(roles));
    }
}
