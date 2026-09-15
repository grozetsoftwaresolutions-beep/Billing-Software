using System.Net.NetworkInformation;
using System.Net.Sockets;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Discovery;

namespace RetailPOS.Infrastructure.Networking;

public sealed class NetworkDiagnosticsService : INetworkDiagnosticsService
{
    public NetworkDiagnosticsDto GetDiagnostics()
    {
        var interfaces = new List<NetworkInterfaceInfoDto>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            var ipProps = ni.GetIPProperties();
            foreach (var unicast in ipProps.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork ||
                    unicast.Address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    string subnet = unicast.IPv4Mask?.ToString() ?? string.Empty;
                    interfaces.Add(new NetworkInterfaceInfoDto(
                        ni.Name,
                        ni.Description,
                        unicast.Address.ToString(),
                        subnet,
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                        ni.SupportsMulticast));
                }
            }
        }

        return new NetworkDiagnosticsDto(
            Environment.MachineName,
            interfaces.AsReadOnly(),
            DiscoveryPort: 5050,
            HttpPort: 5000,
            HttpsPort: 5001);
    }
}
