using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Security;

namespace RetailPOS.Client.Services;

public sealed class AuthenticationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _httpsBaseUrl;

    public string HttpsBaseUrl => _httpsBaseUrl;

    public AuthenticationClient(HttpClient? httpClient = null, string? httpsBaseUrl = null)
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();

        var baseUrl = httpsBaseUrl ?? $"https://localhost:{WindowsServiceDeploymentSpecification.HttpsPort}";
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Authentication requests strictly require HTTPS. Plain HTTP is prohibited for credentials.");
        }

        _httpsBaseUrl = baseUrl.TrimEnd('/');
    }

    private static HttpClient CreateDefaultHttpClient()
    {
#if DEBUG
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
            {
                if (msg.RequestUri?.Host is "localhost" or "127.0.0.1") return true;
                return errors == System.Net.Security.SslPolicyErrors.None;
            }
        };
        return new HttpClient(handler);
#else
        return new HttpClient();
#endif
    }

    public async Task<ApiResponse<LoginResponse>> LoginWithPasswordAsync(
        string username,
        string password,
        Guid? terminalId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);

        var request = new LoginPasswordRequest(username, password, terminalId);
        var url = $"{_httpsBaseUrl}/api/v1/auth/login-password";

        using var response = await _httpClient.PostAsJsonAsync(url, request, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(JsonOptions, cancellationToken);

        return result ?? ApiResponse.Fail<LoginResponse>("Empty response from server.", "EMPTY_RESPONSE");
    }

    public async Task<ApiResponse<LoginResponse>> LoginWithPinAsync(
        string username,
        string pin,
        Guid terminalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        var request = new LoginPinRequest(username, pin, terminalId);
        var url = $"{_httpsBaseUrl}/api/v1/auth/login-pin";

        using var response = await _httpClient.PostAsJsonAsync(url, request, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(JsonOptions, cancellationToken);

        return result ?? ApiResponse.Fail<LoginResponse>("Empty response from server.", "EMPTY_RESPONSE");
    }

    public async Task<ApiResponse<SessionStatusResponse>> ValidateSessionAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var url = $"{_httpsBaseUrl}/api/v1/auth/session";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(req, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<SessionStatusResponse>>(JsonOptions, cancellationToken);

        return result ?? ApiResponse.Fail<SessionStatusResponse>("Invalid session response.", "INVALID_SESSION");
    }

    public async Task<ApiResponse<string>> LockSessionAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var url = $"{_httpsBaseUrl}/api/v1/auth/lock";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(req, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<string>>(JsonOptions, cancellationToken);

        return result ?? ApiResponse.Fail<string>("Failed to lock session.", "LOCK_FAILED");
    }

    public async Task<ApiResponse<TerminalUnlockResponse>> UnlockSessionAsync(
        string token,
        string pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        var request = new TerminalUnlockRequest(pin);
        var url = $"{_httpsBaseUrl}/api/v1/auth/unlock";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(req, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<TerminalUnlockResponse>>(JsonOptions, cancellationToken);

        return result ?? ApiResponse.Fail<TerminalUnlockResponse>("Failed to unlock session.", "UNLOCK_FAILED");
    }

    public async Task<ApiResponse<object>> LogoutAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var url = $"{_httpsBaseUrl}/api/v1/auth/logout";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(req, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOptions, cancellationToken);

        return result ?? ApiResponse.Ok<object>(new { Message = "Logged out." });
    }
}
