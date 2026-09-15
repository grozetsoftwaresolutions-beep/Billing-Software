using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetailPOS.Application.Services;

namespace RetailPOS.Server.BackgroundServices;

internal static partial class UdpDiscoveryLoggerExtensions
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Error processing UDP server discovery request.")]
    public static partial void LogUdpDiscoveryError(this ILogger logger, Exception ex);
}

public sealed class UdpServerDiscoveryListener : BackgroundService
{
    private const int DiscoveryPort = 5105;
    private const string DiscoveryMagic1 = "GROZETPOS_DISCOVER_SERVER_V1";
    private const string DiscoveryMagic2 = "GROZET_DISCOVERY_PROBE";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<UdpServerDiscoveryListener> _logger;

    public UdpServerDiscoveryListener(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        ILogger<UdpServerDiscoveryListener> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // In testing environments or in-memory test servers, skip binding physical UDP sockets
        if (_environment.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await udpClient.ReceiveAsync(stoppingToken);
                    string message = Encoding.UTF8.GetString(result.Buffer).Trim();

                    if (string.Equals(message, DiscoveryMagic1, StringComparison.Ordinal) ||
                        string.Equals(message, DiscoveryMagic2, StringComparison.Ordinal))
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var serverIdentityService = scope.ServiceProvider.GetRequiredService<IServerIdentityService>();
                        var storeService = scope.ServiceProvider.GetRequiredService<IStoreService>();

                        var identity = await serverIdentityService.GetServerIdentityAsync(stoppingToken);
                        if (identity is null) continue;

                        var settingsResponse = await storeService.GetSettingsAsync(stoppingToken);
                        string storeName = settingsResponse.Data?.StoreName ?? "Grozet Retail";

                        var response = new
                        {
                            identity.ServerId,
                            identity.StoreId,
                            StoreName = storeName,
                            identity.ServerFingerprint,
                            identity.PublicKeySpki,
                            HttpBaseUrl = "http://" + result.RemoteEndPoint.Address + ":5000",
                            HttpsBaseUrl = (string?)null,
                            ServerVersion = "1.0.0",
                            ServerTimeUtc = DateTimeOffset.UtcNow
                        };

                        byte[] responseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                        await udpClient.SendAsync(responseBytes, responseBytes.Length, result.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogUdpDiscoveryError(ex);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogUdpDiscoveryError(ex);
        }
    }
}
