using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Discovery;
using RetailPOS.Domain.Common;

namespace RetailPOS.Server.Endpoints;

public static class DiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/discovery");

        group.MapPost("/challenge", async (
            ServerChallengeRequestDto request,
            IServerIdentityService serverIdentityService,
            CancellationToken ct) =>
        {
            try
            {
                var response = await serverIdentityService.SignChallengeAsync(request, ct);
                return Results.Ok(response);
            }
            catch (DomainException ex) when (ex.ErrorCode == "REPLAYED_CHALLENGE_NONCE")
            {
                return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .WithName("SignDiscoveryChallenge")
        .WithSummary("Signs an attestation challenge proving server identity.");

        group.MapGet("/server-info", async (
            IServerIdentityService serverIdentityService,
            IStoreService storeService,
            HttpContext context,
            CancellationToken ct) =>
        {
            var identity = await serverIdentityService.GetServerIdentityAsync(ct);
            var settingsResponse = await storeService.GetSettingsAsync(ct);
            var storeName = settingsResponse.Data?.StoreName ?? "Grozet Retail";
            var host = context.Request.Host.Value;
            var scheme = context.Request.Scheme;

            var version = typeof(DiscoveryEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            var dto = new ServerDiscoveryResponseDto(
                identity?.ServerId ?? "SRV-UNPROVISIONED",
                identity?.StoreId ?? Guid.Empty,
                storeName,
                identity?.ServerFingerprint ?? "UNPROVISIONED",
                identity?.PublicKeySpki ?? string.Empty,
                $"{scheme}://{host}",
                scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? $"{scheme}://{host}" : null,
                version,
                DateTimeOffset.UtcNow,
                ProtocolVersion: ProtocolVersionConstants.CurrentProtocolVersion,
                MinSupportedClientProtocolVersion: ProtocolVersionConstants.MinSupportedClientProtocolVersion);

            return Results.Ok(ApiResponse.Ok(dto));
        })
        .WithName("GetServerInfo")
        .WithSummary("Returns public server identity and network info.");

        group.MapGet("/network-interfaces", (
            INetworkDiagnosticsService networkDiagnostics) =>
        {
            var diagnostics = networkDiagnostics.GetDiagnostics();
            return Results.Ok(ApiResponse.Ok(diagnostics));
        })
        .WithName("GetNetworkInterfaces")
        .WithSummary("Returns network interface and IP configuration.");

        return endpoints;
    }
}
