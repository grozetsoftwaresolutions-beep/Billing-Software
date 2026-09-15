using RetailPOS.Contracts.Discovery;

namespace RetailPOS.Application.Services;

public interface INetworkDiagnosticsService
{
    NetworkDiagnosticsDto GetDiagnostics();
}
