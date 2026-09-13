using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Security;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Server.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth");

        group.MapGet("/setup-status", async (IAuthenticationService authService, CancellationToken ct) =>
        {
            var status = await authService.GetSetupStatusAsync(ct);
            return Results.Ok(ApiResponse.Ok(status));
        })
        .WithName("GetSetupStatus")
        .WithSummary("Checks if first-run store owner setup is required.");

        group.MapPost("/setup-owner", async (SetupOwnerRequest request, IAuthenticationService authService, CancellationToken ct) =>
        {
            var result = await authService.SetupOwnerAsync(request, ct);
            return Results.Ok(result);
        })
        .WithName("SetupOwner")
        .WithSummary("Initializes store owner account on first run (race-safe).");

        group.MapPost("/login-password", async (LoginPasswordRequest request, IAuthenticationService authService, CancellationToken ct) =>
        {
            var result = await authService.LoginWithPasswordAsync(request, ct);
            return result.Success
                ? Results.Ok(result)
                : Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
        })
        .WithName("LoginPassword")
        .WithSummary("Authenticates Manager, Admin, or Owner via password.");

        group.MapPost("/login-pin", async (LoginPinRequest request, IAuthenticationService authService, CancellationToken ct) =>
        {
            var result = await authService.LoginWithPinAsync(request, ct);
            return result.Success
                ? Results.Ok(result)
                : Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
        })
        .WithName("LoginPin")
        .WithSummary("Authenticates Cashier via POS counter PIN with lockout protection.");

        group.MapPost("/logout", async (HttpContext context, IAuthenticationService authService, CancellationToken ct) =>
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader["Bearer ".Length..].Trim();
                await authService.LogoutAsync(token, ct);
            }

            return Results.Ok(ApiResponse.Ok(new { Message = "Logged out successfully." }));
        })
        .WithName("Logout")
        .WithSummary("Revokes active user session.");

        group.MapGet("/session", async (HttpContext context, ISessionManager sessionManager, CancellationToken ct) =>
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(
                    ApiResponse.Fail<SessionStatusResponse>("Missing or invalid Authorization header.", "UNAUTHENTICATED"),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var token = authHeader["Bearer ".Length..].Trim();
            var validation = await sessionManager.ValidateSessionAsync(token, ct);

            if (!validation.IsValid)
            {
                return Results.Json(
                    ApiResponse.Fail<SessionStatusResponse>(validation.ErrorMessage ?? "Invalid session.", "UNAUTHENTICATED"),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var response = new SessionStatusResponse(
                true,
                validation.UserId,
                validation.Username,
                validation.FullName,
                validation.Role,
                validation.TerminalId,
                validation.ExpiresAtUtc);

            return Results.Ok(ApiResponse.Ok(response));
        })
        .WithName("GetSessionStatus")
        .WithSummary("Validates current bearer token and returns session status.");

        return endpoints;
    }
}
