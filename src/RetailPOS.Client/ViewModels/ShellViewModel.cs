using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Client.Services;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Client.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AuthenticationClient _authClient = new();

    [ObservableProperty]
    public partial string AppTitle { get; set; } = "GROZET RETAILPOS";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = "Retail Management System";

    [ObservableProperty]
    public partial string CurrentModuleName { get; set; } = "Home / Dashboard";

    [ObservableProperty]
    public partial string SessionStatusText { get; set; } = "Session: Not Authenticated";

    [ObservableProperty]
    public partial bool IsUserAuthenticated { get; set; }

    [ObservableProperty]
    public partial bool IsSessionLocked { get; set; }

    [ObservableProperty]
    public partial string TerminalStatusText { get; set; } = "Terminal: Not Enrolled";

    [ObservableProperty]
    public partial bool IsTerminalReady { get; set; }

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

    public ShellViewModel()
    {
        ClientSessionManager.Instance.StateChanged += OnSessionStateChanged;
        UpdateSessionPresentation(ClientSessionManager.Instance.State);
    }

    private void OnSessionStateChanged(ClientSessionState state)
    {
        UpdateSessionPresentation(state);
    }

    private void UpdateSessionPresentation(ClientSessionState state)
    {
        var session = ClientSessionManager.Instance;
        switch (state)
        {
            case ClientSessionState.Active:
                SessionStatusText = $"Session: {session.CurrentFullName ?? session.CurrentUsername} ({session.CurrentRole})";
                IsUserAuthenticated = true;
                IsSessionLocked = false;
                break;

            case ClientSessionState.Locked:
                SessionStatusText = $"Session Locked ({session.CurrentUsername})";
                IsUserAuthenticated = true;
                IsSessionLocked = true;
                break;

            case ClientSessionState.Expired:
                SessionStatusText = "Session: Expired";
                IsUserAuthenticated = false;
                IsSessionLocked = false;
                break;

            case ClientSessionState.NotAuthenticated:
            default:
                SessionStatusText = "Session: Not Authenticated";
                IsUserAuthenticated = false;
                IsSessionLocked = false;
                break;
        }
    }

    public async Task LockAsync(CancellationToken cancellationToken = default)
    {
        var token = ClientSessionManager.Instance.CurrentToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                await _authClient.LockSessionAsync(token, cancellationToken);
            }
            catch
            {
                // Best-effort server lock notification
            }
        }

        ClientSessionManager.Instance.TransitionToLocked();
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var token = ClientSessionManager.Instance.CurrentToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                await _authClient.LogoutAsync(token, cancellationToken);
            }
            catch
            {
                // Best-effort remote revocation
            }
        }

        ClientSessionManager.Instance.ClearSession(ClientSessionState.NotAuthenticated);
    }

    public void RefreshTerminalStatus()
    {
        try
        {
            using var probeClient = new HttpClient();
            var enrollmentClient = new TerminalEnrollmentClient(probeClient);
            var pairingToken = enrollmentClient.LoadPairingToken();

            if (!string.IsNullOrWhiteSpace(pairingToken))
            {
                IsTerminalReady = true;
                TerminalStatusText = "Terminal: Ready";
            }
            else
            {
                IsTerminalReady = false;
                TerminalStatusText = "Terminal: Not Enrolled";
            }
        }
        catch
        {
            IsTerminalReady = false;
            TerminalStatusText = "Terminal: Not Enrolled";
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusMessage = "Checking server connection...";
        ServerStatusText = "Server: Checking...";
        ServerEndpoint = "Probing LAN & localhost...";

        RefreshTerminalStatus();

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

            // 1. Probe local development / server HTTPS endpoints (port 5101) first: 127.0.0.1 and localhost
            string[] localHttpsUrls =
            [
                $"https://127.0.0.1:{WindowsServiceDeploymentSpecification.HttpsPort}",
                $"https://localhost:{WindowsServiceDeploymentSpecification.HttpsPort}"
            ];

            foreach (var httpsBaseUrl in localHttpsUrls)
            {
                try
                {
                    using var httpsResponse = await httpClient.GetAsync($"{httpsBaseUrl}/api/v1/health", cancellationToken);
                    if (httpsResponse.IsSuccessStatusCode)
                    {
                        IsServerConnected = true;
                        ServerStatusText = "Server: Online";
                        ServerEndpoint = httpsBaseUrl;
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
                    // Proceed to next local HTTPS candidate
                }
            }

            // 2. Probe local HTTP fallback (port 5100)
            string[] localHttpUrls =
            [
                $"http://127.0.0.1:{WindowsServiceDeploymentSpecification.HttpPort}",
                $"http://localhost:{WindowsServiceDeploymentSpecification.HttpPort}"
            ];

            foreach (var httpBaseUrl in localHttpUrls)
            {
                try
                {
                    using var httpResponse = await httpClient.GetAsync($"{httpBaseUrl}/api/v1/health", cancellationToken);
                    if (httpResponse.IsSuccessStatusCode)
                    {
                        IsServerConnected = true;
                        ServerStatusText = "Server: Online";
                        ServerEndpoint = httpBaseUrl;
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
                    // Proceed to next local HTTP candidate
                }
            }

            // 3. Fall back to UDP broadcast discovery for remote LAN server
            try
            {
                var discovered = await ServerDiscoveryClient.DiscoverServerUdpAsync(1000, cancellationToken);
                var serverUrl = discovered?.HttpsBaseUrl ?? discovered?.HttpBaseUrl;
                if (discovered != null && !string.IsNullOrWhiteSpace(serverUrl))
                {
                    using var lanResponse = await httpClient.GetAsync($"{serverUrl.TrimEnd('/')}/api/v1/health", cancellationToken);
                    if (lanResponse.IsSuccessStatusCode)
                    {
                        IsServerConnected = true;
                        ServerStatusText = "Server: Online";
                        ServerEndpoint = serverUrl;
                        StatusMessage = $"Connected to LAN server at {serverUrl}";
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // UDP discovery probe failed
            }

            // 4. Graceful offline fallback
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
