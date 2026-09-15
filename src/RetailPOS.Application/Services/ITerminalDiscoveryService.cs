using RetailPOS.Contracts.Discovery;

namespace RetailPOS.Application.Services;

public interface ITerminalDiscoveryService
{
    Task<ServerDiscoveryResponseDto> GetDiscoveryInfoAsync(CancellationToken ct = default);
    Task<NetworkDiagnosticsDto> GetNetworkDiagnosticsAsync(CancellationToken ct = default);
}
