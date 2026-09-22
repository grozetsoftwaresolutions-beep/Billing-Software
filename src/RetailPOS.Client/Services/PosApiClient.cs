using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Contracts.Sales;

namespace RetailPOS.Client.Services;

public sealed class PosApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _httpsBaseUrl;
    private readonly TerminalEnrollmentClient _terminalEnrollmentClient;

    public string HttpsBaseUrl => _httpsBaseUrl;

    public PosApiClient(
        HttpClient? httpClient = null,
        string? httpsBaseUrl = null,
        TerminalEnrollmentClient? terminalEnrollmentClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();

        var baseUrl = httpsBaseUrl ?? $"https://localhost:{WindowsServiceDeploymentSpecification.HttpsPort}";
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("POS API requests strictly require HTTPS. Plain HTTP is prohibited.");
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

        if (attachTerminalHeaders)
        {
            var effectiveTerminalId = terminalId ?? ClientSessionManager.Instance.CurrentTerminalId;
            if (effectiveTerminalId.HasValue && effectiveTerminalId.Value != Guid.Empty)
            {
                request.Headers.TryAddWithoutValidation("X-Terminal-ID", effectiveTerminalId.Value.ToString());
            }

            request.Headers.TryAddWithoutValidation(
                ProtocolVersionConstants.ClientProtocolVersionHeader,
                ProtocolVersionConstants.CurrentProtocolVersion.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(ProtocolVersionConstants.ClientVersionHeader, "1.0.0");

            var pairingToken = _terminalEnrollmentClient.LoadPairingToken();
            if (!string.IsNullOrWhiteSpace(pairingToken))
            {
                request.Headers.TryAddWithoutValidation("X-Terminal-Token", pairingToken);
            }
        }
    }

    public async Task<ApiResponse<ProductPosDto>> GetProductByBarcodeAsync(
        string barcode,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(barcode);
        var url = $"{_httpsBaseUrl}/api/v1/products/barcode/{Uri.EscapeDataString(barcode.Trim())}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return ApiResponse.Fail<ProductPosDto>("Product not found for barcode.", "PRODUCT_NOT_FOUND");
        }

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ProductPosDto>>(JsonOptions, ct);
        return result ?? ApiResponse.Fail<ProductPosDto>("Empty response from server.", "EMPTY_RESPONSE");
    }

    public async Task<ApiResponse<PagedResult<ProductPosDto>>> SearchProductsAsync(
        string query,
        int page = 1,
        int pageSize = 20,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/products/search?query={Uri.EscapeDataString(query ?? string.Empty)}&page={page}&pageSize={pageSize}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProductPosDto>>>(JsonOptions, ct);
        return result ?? ApiResponse.Fail<PagedResult<ProductPosDto>>("Empty response from server.", "EMPTY_RESPONSE");
    }

    public async Task<ApiResponse<ProductPosDto>> GetProductByIdAsync(
        Guid id,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/products/pos/{id}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ProductPosDto>>(JsonOptions, ct);
        return result ?? ApiResponse.Fail<ProductPosDto>("Empty response from server.", "EMPTY_RESPONSE");
    }

    public async Task<ApiResponse<CartCalculationDto>> CalculateCartAsync(
        CalculateSaleRequest calculationRequest,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calculationRequest);
        var url = $"{_httpsBaseUrl}/api/v1/sales/calculate";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(calculationRequest)
        };
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<CartCalculationDto>>(JsonOptions, ct);
        return result ?? ApiResponse.Fail<CartCalculationDto>("Empty calculation response.", "EMPTY_RESPONSE");
    }

    public async Task<ApiResponse<SaleInvoiceDto>> CreateSaleAsync(
        CreateSaleRequest saleRequest,
        Guid? terminalId = null,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(saleRequest);
        var url = $"{_httpsBaseUrl}/api/v1/sales";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(saleRequest)
        };
        ApplySecurityHeaders(request, token, attachTerminalHeaders: true, terminalId: terminalId);

        using var response = await _httpClient.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>(JsonOptions, ct);
        return result ?? ApiResponse.Fail<SaleInvoiceDto>("Empty checkout response.", "EMPTY_RESPONSE");
    }

    public async Task<ApiResponse<ReceiptPrintResponseDto>> PrintReceiptAsync(
        Guid saleInvoiceId,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/sales/{saleInvoiceId}/print";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        ApplySecurityHeaders(request, token, attachTerminalHeaders: true);

        using var response = await _httpClient.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ReceiptPrintResponseDto>>(JsonOptions, ct);
        return result ?? ApiResponse.Fail<ReceiptPrintResponseDto>("Empty receipt print response.", "EMPTY_RESPONSE");
    }

    public async Task<ApiResponse<object>> ReportDrawerKickResultAsync(
        ReportDrawerKickResultRequest kickRequest,
        Guid? terminalId = null,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kickRequest);
        var url = $"{_httpsBaseUrl}/api/v1/hardware/drawer/report-kick-result";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(kickRequest)
        };
        ApplySecurityHeaders(request, token, attachTerminalHeaders: true, terminalId: terminalId);

        using var response = await _httpClient.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOptions, ct);
        return result ?? ApiResponse.Ok<object>(new { Reported = true });
    }
}
