using RetailPOS.Contracts.Discovery;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Application.Services;

public interface IServerIdentityService
{
    ServerState CurrentState { get; }
    Task<ServerIdentityInfo?> GetServerIdentityAsync(CancellationToken ct = default);
    Task<ServerState> EvaluateStartupStateAsync(CancellationToken ct = default);
    Task<ServerIdentityInfo> ProvisionInitialIdentityAsync(CancellationToken ct = default);
    Task<ProvisionReplacementIdentityResponseDto> ProvisionReplacementIdentityAsync(ProvisionReplacementIdentityRequestDto request, CancellationToken ct = default);
    Task<ServerChallengeResponseDto> SignChallengeAsync(ServerChallengeRequestDto request, CancellationToken ct = default);
    Task ReconcilePostCrashAuditAsync(CancellationToken ct = default);
}
