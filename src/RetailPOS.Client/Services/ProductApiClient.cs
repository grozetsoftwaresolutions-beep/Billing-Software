using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Client.Services;

/// <summary>
/// Typed HTTPS client for Product catalog and Category/Brand management endpoints.
/// Strictly reuses existing ClientSessionManager and port 5101 TLS infrastructure.
/// </summary>
public sealed class ProductApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _httpsBaseUrl;

    public string HttpsBaseUrl => _httpsBaseUrl;

    public ProductApiClient(HttpClient? httpClient = null, string? httpsBaseUrl = null)
    {
        _httpClient = httpClient ?? new HttpClient();

        var baseUrl = httpsBaseUrl ?? $"https://127.0.0.1:{WindowsServiceDeploymentSpecification.HttpsPort}";
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Product catalog API requests strictly require HTTPS. Plain HTTP is prohibited.");
        }

        _httpsBaseUrl = baseUrl.TrimEnd('/');
    }

    private static void ApplySecurityHeaders(HttpRequestMessage request, string? token)
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
    }

    public async Task<ApiResponse<PagedResult<ProductManagementDto>>> GetProductsManagementAsync(
        int? categoryId = null,
        int? brandId = null,
        bool? activeOnly = null,
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

        if (categoryId.HasValue) queryParams.Add($"categoryId={categoryId.Value}");
        if (brandId.HasValue) queryParams.Add($"brandId={brandId.Value}");
        if (activeOnly.HasValue) queryParams.Add($"activeOnly={activeOnly.Value.ToString().ToLowerInvariant()}");

        var url = $"{_httpsBaseUrl}/api/v1/products?{string.Join("&", queryParams)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<PagedResult<ProductManagementDto>>(response, ct);
    }

    public async Task<ApiResponse<ProductManagementDto>> GetProductManagementByIdAsync(
        Guid id,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/products/{id}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<ProductManagementDto>(response, ct);
    }

    public async Task<ApiResponse<ProductManagementDto>> CreateProductAsync(
        CreateProductRequest requestPayload,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestPayload);

        var url = $"{_httpsBaseUrl}/api/v1/products";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestPayload, options: JsonOptions)
        };
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<ProductManagementDto>(response, ct);
    }

    public async Task<ApiResponse<ProductManagementDto>> UpdateProductAsync(
        Guid id,
        UpdateProductRequest requestPayload,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestPayload);

        var url = $"{_httpsBaseUrl}/api/v1/products/{id}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(requestPayload, options: JsonOptions)
        };
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<ProductManagementDto>(response, ct);
    }

    public async Task<ApiResponse<ProductBarcodeDto>> AddBarcodeAsync(
        Guid productId,
        AddBarcodeRequest requestPayload,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestPayload);

        var url = $"{_httpsBaseUrl}/api/v1/products/{productId}/barcodes";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestPayload, options: JsonOptions)
        };
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<ProductBarcodeDto>(response, ct);
    }

    public async Task<ApiResponse<ProductManagementDto>> SetProductStatusAsync(
        Guid productId,
        bool isActive,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/products/{productId}/status?isActive={isActive.ToString().ToLowerInvariant()}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<ProductManagementDto>(response, ct);
    }

    public async Task<ApiResponse<IReadOnlyList<CategoryDto>>> GetCategoriesAsync(
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/categories";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<IReadOnlyList<CategoryDto>>(response, ct);
    }

    public async Task<ApiResponse<CategoryDto>> CreateCategoryAsync(
        CreateCategoryRequest requestPayload,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestPayload);

        var url = $"{_httpsBaseUrl}/api/v1/categories";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestPayload, options: JsonOptions)
        };
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CategoryDto>(response, ct);
    }

    public async Task<ApiResponse<IReadOnlyList<BrandDto>>> GetBrandsAsync(
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/brands";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<IReadOnlyList<BrandDto>>(response, ct);
    }

    public async Task<ApiResponse<BrandDto>> CreateBrandAsync(
        CreateBrandRequest requestPayload,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestPayload);

        var url = $"{_httpsBaseUrl}/api/v1/brands";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestPayload, options: JsonOptions)
        };
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<BrandDto>(response, ct);
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
