using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;

namespace RetailPOS.E2E.Tests.Fixtures;

/// <summary>
/// Represents a virtual POS counter terminal client with its own network identity,
/// HTTP headers (X-Terminal-ID, X-Terminal-Token, X-Test-Remote-IP), authentication session state,
/// hardware device references, and deterministic fault-injection handler.
/// </summary>
public sealed class VirtualPosTerminal : IDisposable
{
    public Guid TerminalId { get; }
    public string TerminalCode { get; }
    public string TerminalName { get; }
    public IPAddress IpAddress { get; }
    public string PairingToken { get; }
    public string? SessionToken { get; private set; }
    public LoginResponse? CurrentSession { get; private set; }
    public HttpClient Client { get; }
    public FaultInjectingHttpMessageHandler FaultHandler { get; }
    public MockHardwareDeviceManager Hardware { get; }

    public VirtualPosTerminal(
        Guid terminalId,
        string terminalCode,
        string terminalName,
        IPAddress ipAddress,
        string pairingToken,
        HttpClient client,
        FaultInjectingHttpMessageHandler faultHandler,
        MockHardwareDeviceManager hardware)
    {
        TerminalId = terminalId;
        TerminalCode = terminalCode;
        TerminalName = terminalName;
        IpAddress = ipAddress;
        PairingToken = pairingToken;
        Client = client ?? throw new ArgumentNullException(nameof(client));
        FaultHandler = faultHandler ?? throw new ArgumentNullException(nameof(faultHandler));
        Hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));

        ApplyTerminalHeaders();
    }

    private void ApplyTerminalHeaders()
    {
        Client.DefaultRequestHeaders.Remove("X-Terminal-ID");
        Client.DefaultRequestHeaders.Add("X-Terminal-ID", TerminalId.ToString());

        Client.DefaultRequestHeaders.Remove("X-Terminal-Token");
        Client.DefaultRequestHeaders.Add("X-Terminal-Token", PairingToken);

        Client.DefaultRequestHeaders.Remove("X-Test-Remote-IP");
        Client.DefaultRequestHeaders.Add("X-Test-Remote-IP", IpAddress.ToString());
    }

    public void SetBearerToken(string? token)
    {
        SessionToken = token;
        if (string.IsNullOrWhiteSpace(token))
        {
            Client.DefaultRequestHeaders.Authorization = null;
        }
        else
        {
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<bool> LoginPasswordAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var req = new LoginPasswordRequest(username, password);
        var res = await Client.PostAsJsonAsync("/api/v1/auth/login-password", req, cancellationToken);
        if (!res.IsSuccessStatusCode)
            return false;

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(cancellationToken: cancellationToken);
        if (body?.Success == true && body.Data?.Token != null)
        {
            SetBearerToken(body.Data.Token);
            CurrentSession = body.Data;
            return true;
        }

        return false;
    }

    public async Task<bool> LoginPinAsync(string username, string pin, CancellationToken cancellationToken = default)
    {
        var req = new LoginPinRequest(username, pin, TerminalId);
        var res = await Client.PostAsJsonAsync("/api/v1/auth/login-pin", req, cancellationToken);
        if (!res.IsSuccessStatusCode)
            return false;

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(cancellationToken: cancellationToken);
        if (body?.Success == true && body.Data?.Token != null)
        {
            SetBearerToken(body.Data.Token);
            CurrentSession = body.Data;
            return true;
        }

        return false;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(SessionToken))
        {
            try
            {
                await Client.PostAsync("/api/v1/auth/logout", null, cancellationToken);
            }
            catch
            {
                // Best-effort logout
            }
        }

        SetBearerToken(null);
        CurrentSession = null;
    }

    public Task<HttpResponseMessage> PostAsJsonAsync<T>(string uri, T value, CancellationToken cancellationToken = default)
    {
        return Client.PostAsJsonAsync(uri, value, cancellationToken);
    }

    public Task<HttpResponseMessage> GetAsync(string uri, CancellationToken cancellationToken = default)
    {
        return Client.GetAsync(uri, cancellationToken);
    }

    public void Dispose()
    {
        Client.Dispose();
        FaultHandler.Dispose();
    }
}
