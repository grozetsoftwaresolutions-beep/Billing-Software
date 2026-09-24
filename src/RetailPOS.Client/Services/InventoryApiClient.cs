using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Inventory;

namespace RetailPOS.Client.Services;

/// <summary>
/// Typed HTTPS client for Inventory stock balances, low-stock alerts, movement ledger, and manual adjustments.
/// Strictly reuses existing ClientSessionManager, TerminalEnrollmentClient, and port 5101 TLS infrastructure.
/// </summary>
public sealed class InventoryApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _httpsBaseUrl;
    private readonly TerminalEnrollmentClient _terminalEnrollmentClient;

    public string HttpsBaseUrl => _httpsBaseUrl;

    public InventoryApiClient(
        HttpClient? httpClient = null,
        string? httpsBaseUrl = null,
        TerminalEnrollmentClient? terminalEnrollmentClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();

        var baseUrl = httpsBaseUrl ?? $"https://localhost:{WindowsServiceDeploymentSpecification.HttpsPort}";
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Inventory API requests strictly require HTTPS. Plain HTTP is prohibited.");
        }

        _httpsBaseUrl = baseUrl.TrimEnd('/');
        _terminalEnrollmentClient = terminalEnrollmentClient ?? new TerminalEnrollmentClient(_httpClient);
    }

    private void ApplySecurityHeaders(HttpRequestMessage request, string? token, bool attachTerminalHeaders = false, Guid? terminalId = null)
    {
        var activeToken = token ?? ClientSessionManager.Instance.CurrentToken;
        if (!string.IsNullOrWhiteSpace(activeToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", activeToken);
        }

        request.Headers.TryAddWithoutValidation(
            ProtocolVersionConstants.ClientProtocolVersionHeader,
            ProtocolVersionConstants.CurrentProtocolVersion.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(ProtocolVersionConstants.ClientVersionHeader, "1.0.0");

        if (attachTerminalHeaders)
        {
            var effectiveTerminalId = terminalId ?? ClientSessionManager.Instance.CurrentTerminalId;
            if (effectiveTerminalId.HasValue && effectiveTerminalId.Value != Guid.Empty)
            {
                request.Headers.TryAddWithoutValidation("X-Terminal-ID", effectiveTerminalId.Value.ToString());
            }

            var pairingToken = _terminalEnrollmentClient.LoadPairingToken();
            if (!string.IsNullOrWhiteSpace(pairingToken))
            {
                request.Headers.TryAddWithoutValidation("X-Terminal-Token", pairingToken);
            }
        }
    }

    public async Task<ApiResponse<StockBalanceDto>> GetStockBalanceAsync(
        Guid productId,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/inventory/stock/{productId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<StockBalanceDto>(response, ct);
    }

    public async Task<ApiResponse<PagedResult<StockBalanceDto>>> GetLowStockProductsAsync(
        int page = 1,
        int pageSize = 20,
        string? token = null,
        CancellationToken ct = default)
    {
        var clampedPage = Math.Max(1, page);
        var clampedSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 50);

        var url = $"{_httpsBaseUrl}/api/v1/inventory/low-stock?page={clampedPage}&pageSize={clampedSize}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<PagedResult<StockBalanceDto>>(response, ct);
    }

    public async Task<ApiResponse<PagedResult<StockMovementDto>>> GetStockMovementsAsync(
        Guid? productId = null,
        int page = 1,
        int pageSize = 20,
        string? token = null,
        CancellationToken ct = default)
    {
        var clampedPage = Math.Max(1, page);
        var clampedSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 50);

        var queryParams = new List<string>
        {
            $"page={clampedPage}",
            $"pageSize={clampedSize}"
        };

        if (productId.HasValue && productId.Value != Guid.Empty)
        {
            queryParams.Add($"productId={productId.Value}");
        }

        var url = $"{_httpsBaseUrl}/api/v1/inventory/movements?{string.Join("&", queryParams)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<PagedResult<StockMovementDto>>(response, ct);
    }

    public async Task<ApiResponse<StockMovementDto>> AdjustStockAsync(
        StockAdjustmentRequest requestPayload,
        Guid? terminalId = null,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestPayload);

        var url = $"{_httpsBaseUrl}/api/v1/inventory/adjustments";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestPayload, options: JsonOptions)
        };
        ApplySecurityHeaders(request, token, attachTerminalHeaders: true, terminalId: terminalId);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<StockMovementDto>(response, ct);
    }

    private static async Task<ApiResponse<T>> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>(JsonOptions, ct);
            if (body != null) return body;
        }
        catch
        {
            // fallback if body cannot be parsed
        }

        if (!response.IsSuccessStatusCode)
        {
            return ApiResponse.Fail<T>($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}", response.StatusCode.ToString());
        }

        return ApiResponse.Fail<T>("Empty response received from store server.", "EMPTY_RESPONSE");
    }
}
