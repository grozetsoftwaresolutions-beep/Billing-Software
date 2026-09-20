using System.Net.Http.Json;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.Services;

public sealed class ServerConnectionMonitor : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ServerDiscoveryClient _discoveryClient;
    private readonly Guid _terminalId;
    private readonly string _serverBaseUrl;
    private readonly TimeSpan _heartbeatInterval;
    private PeriodicTimer? _timer;
    private Task? _monitorTask;
    private CancellationTokenSource? _cts;
    private int _consecutiveFailures;

    public CounterConnectivityStatus ConnectivityStatus { get; private set; } = CounterConnectivityStatus.Disconnected;
    public DateTime? LastSuccessfulHeartbeatUtc { get; private set; }

    public event Action<CounterConnectivityStatus>? StatusChanged;
    public event Action<string>? IdentityMismatchDetected;
    public event Action<string>? ProtocolVersionMismatchDetected;

    public ServerConnectionMonitor(
        HttpClient httpClient,
        ServerDiscoveryClient discoveryClient,
        Guid terminalId,
        string serverBaseUrl,
        TimeSpan? heartbeatInterval = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _discoveryClient = discoveryClient ?? throw new ArgumentNullException(nameof(discoveryClient));
        _terminalId = terminalId;
        _serverBaseUrl = serverBaseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(serverBaseUrl));
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(5);
    }

    public void Start()
    {
        if (_cts != null) return;

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(_heartbeatInterval);
        _monitorTask = RunMonitorLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;

        _cts.Cancel();
        if (_monitorTask != null)
        {
            try { await _monitorTask; } catch (OperationCanceledException) { }
        }
        _timer?.Dispose();
        _cts.Dispose();
        _cts = null;
        _timer = null;
        _monitorTask = null;
    }

    public async Task SendHeartbeatOnceAsync(CancellationToken cancellationToken = default)
    {
        // If client is locked due to identity mismatch, do not send normal heartbeats
        if (_discoveryClient.SecurityState == ClientSecurityState.IdentityMismatchLocked)
        {
            IdentityMismatchDetected?.Invoke("Client is locked due to server identity mismatch.");
            UpdateStatus(CounterConnectivityStatus.Disconnected);
            return;
        }

        try
        {
            var dto = new TerminalHeartbeatDto(
                _terminalId,
                Environment.MachineName,
                null,
                DateTimeOffset.UtcNow);

            var url = $"{_serverBaseUrl}/api/v1/terminals/heartbeat";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(dto)
            };
            request.Headers.TryAddWithoutValidation(ProtocolVersionConstants.ClientProtocolVersionHeader, ProtocolVersionConstants.CurrentProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(ProtocolVersionConstants.ClientVersionHeader, "1.0.0");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _consecutiveFailures = 0;
                LastSuccessfulHeartbeatUtc = DateTime.UtcNow;
                UpdateStatus(CounterConnectivityStatus.Online);
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.UpgradeRequired)
            {
                _consecutiveFailures++;
                ProtocolVersionMismatchDetected?.Invoke("Client protocol is deprecated. Upgrade required.");
                UpdateStatus(CounterConnectivityStatus.Disconnected);
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _consecutiveFailures++;
                ProtocolVersionMismatchDetected?.Invoke("Client protocol is newer than server. Server upgrade required.");
                UpdateStatus(CounterConnectivityStatus.Disconnected);
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // Identity mismatch or decommissioned
                _consecutiveFailures++;
                IdentityMismatchDetected?.Invoke("Forbidden response received from server during heartbeat.");
                UpdateStatus(CounterConnectivityStatus.Disconnected);
            }
            else
            {
                HandleFailure();
            }
        }
        catch
        {
            HandleFailure();
        }
    }

    private void HandleFailure()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= 5)
        {
            UpdateStatus(CounterConnectivityStatus.Offline);
        }
        else
        {
            UpdateStatus(CounterConnectivityStatus.Disconnected);
        }
    }

    private void UpdateStatus(CounterConnectivityStatus newStatus)
    {
        if (ConnectivityStatus != newStatus)
        {
            ConnectivityStatus = newStatus;
            StatusChanged?.Invoke(newStatus);
        }
    }

    private async Task RunMonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _timer != null)
        {
            try
            {
                if (await _timer.WaitForNextTickAsync(cancellationToken))
                {
                    await SendHeartbeatOnceAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                HandleFailure();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
