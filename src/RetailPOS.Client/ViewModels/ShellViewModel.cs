using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Client.Services;

namespace RetailPOS.Client.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string AppTitle { get; set; } = "GROZET RETAILPOS";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = "Retail Management System";

    [ObservableProperty]
    public partial string CurrentModuleName { get; set; } = "Home / Dashboard";

    [ObservableProperty]
    public partial string SessionStatusText { get; set; } = "Session: Not Authenticated";

    [ObservableProperty]
    public partial string TerminalStatusText { get; set; } = "Terminal: Ready";

    [ObservableProperty]
    public partial string ServerStatusText { get; set; } = "Server: Checking...";

    [ObservableProperty]
    public partial string ServerEndpoint { get; set; } = "Probing...";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Application shell initialized.";

    [ObservableProperty]
    public partial string VersionInfo { get; set; } = "v1.0.0";

    [ObservableProperty]
    public partial string MachineName { get; set; } = Environment.MachineName;

    [ObservableProperty]
    public partial string Architecture { get; set; } = Environment.Is64BitProcess ? "x64" : "x86";

    [ObservableProperty]
    public partial string RuntimeTarget { get; set; } = ".NET 10 (Windows 10/11 x64)";

    [ObservableProperty]
    public partial bool IsServerConnected { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusMessage = "Checking server connection...";
        ServerStatusText = "Server: Checking...";
        ServerEndpoint = "Probing LAN & localhost...";

        try
        {
            // 1. Attempt UDP broadcast discovery with a short timeout
            var discovered = await ServerDiscoveryClient.DiscoverServerUdpAsync(1000, cancellationToken);
            var serverUrl = discovered?.HttpsBaseUrl ?? discovered?.HttpBaseUrl;
            if (discovered != null && !string.IsNullOrWhiteSpace(serverUrl))
            {
                IsServerConnected = true;
                ServerStatusText = "Server: Connected";
                ServerEndpoint = serverUrl;
                StatusMessage = $"Connected to LAN server at {serverUrl}";
                return;
            }

            // 2. Check local server fallback at http://localhost:5000/health
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = await httpClient.GetAsync("http://localhost:5000/health", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                IsServerConnected = true;
                ServerStatusText = "Server: Connected";
                ServerEndpoint = "http://localhost:5000";
                StatusMessage = "Connected to local server on port 5000.";
                return;
            }

            // 3. Graceful offline fallback
            IsServerConnected = false;
            ServerStatusText = "Server: Offline";
            ServerEndpoint = "Offline (Standalone)";
            StatusMessage = "Server unreachable. Client presentation shell ready in offline mode.";
        }
        catch (OperationCanceledException)
        {
            // Gracefully handled cancellation
        }
        catch
        {
            IsServerConnected = false;
            ServerStatusText = "Server: Offline";
            ServerEndpoint = "Offline (Standalone)";
            StatusMessage = "Server unreachable. Client presentation shell ready in offline mode.";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
