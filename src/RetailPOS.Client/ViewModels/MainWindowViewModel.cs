using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Client.Services;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Client.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string AppTitle { get; set; } = "GROZET RETAILPOS";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = "Retail Management System";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Client is starting...";

    [ObservableProperty]
    public partial string ServerStatusText { get; set; } = "Server: Checking...";

    [ObservableProperty]
    public partial string TerminalStatusText { get; set; } = "Terminal: Not Enrolled";

    [ObservableProperty]
    public partial string VersionInfo { get; set; } = "v1.0.0";

    [ObservableProperty]
    public partial string MachineName { get; set; } = Environment.MachineName;

    [ObservableProperty]
    public partial string Architecture { get; set; } = Environment.Is64BitProcess ? "x64" : "x86";

    [ObservableProperty]
    public partial bool IsServerConnected { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusMessage = "Checking server connection...";
        ServerStatusText = "Server: Checking...";

        try
        {
            // 1. Attempt UDP broadcast discovery with a short timeout on port 5105
            var discovered = await ServerDiscoveryClient.DiscoverServerUdpAsync(1000, cancellationToken);
            var serverUrl = discovered?.HttpsBaseUrl ?? discovered?.HttpBaseUrl;
            if (discovered != null && !string.IsNullOrWhiteSpace(serverUrl))
            {
                IsServerConnected = true;
                ServerStatusText = "Server: Connected";
                StatusMessage = $"Connected to server at {serverUrl}";
                return;
            }

            // 2. Check local server fallback using authoritative ports (5101 HTTPS / 5100 HTTP) and /api/v1/health
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };

            var httpsBaseUrl = $"https://localhost:{WindowsServiceDeploymentSpecification.HttpsPort}";
            try
            {
                using var httpsResponse = await httpClient.GetAsync($"{httpsBaseUrl}/api/v1/health", cancellationToken);
                if (httpsResponse.IsSuccessStatusCode)
                {
                    IsServerConnected = true;
                    ServerStatusText = "Server: Connected";
                    StatusMessage = $"Connected to local server on port {WindowsServiceDeploymentSpecification.HttpsPort} (HTTPS).";
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Fallback to HTTP
            }

            var httpBaseUrl = $"http://localhost:{WindowsServiceDeploymentSpecification.HttpPort}";
            try
            {
                using var httpResponse = await httpClient.GetAsync($"{httpBaseUrl}/api/v1/health", cancellationToken);
                if (httpResponse.IsSuccessStatusCode)
                {
                    IsServerConnected = true;
                    ServerStatusText = "Server: Connected";
                    StatusMessage = $"Connected to local server on port {WindowsServiceDeploymentSpecification.HttpPort} (HTTP).";
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Local HTTP unreachable
            }

            // 3. Fallback: Offline / Standalone
            IsServerConnected = false;
            ServerStatusText = "Server: Unavailable";
            StatusMessage = "Server unreachable. Client ready in offline mode.";
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        catch
        {
            IsServerConnected = false;
            ServerStatusText = "Server: Unavailable";
            StatusMessage = "Server unreachable. Client ready in offline mode.";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
