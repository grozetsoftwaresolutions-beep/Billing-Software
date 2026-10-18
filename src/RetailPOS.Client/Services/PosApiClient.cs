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
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Configuration;
using RetailPOS.Contracts.Customers;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Contracts.Reports;
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

    public async Task<ApiResponse<PagedResult<SaleSummaryDto>>> SearchSalesAsync(
        SaleSearchRequest searchRequest,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(searchRequest);

        var queryParams = new List<string>
        {
            $"page={searchRequest.Page}",
            $"pageSize={searchRequest.PageSize}"
        };

        if (searchRequest.FromUtc.HasValue)
        {
            queryParams.Add($"fromUtc={Uri.EscapeDataString(searchRequest.FromUtc.Value.ToString("o"))}");
        }

        if (searchRequest.ToUtc.HasValue)
        {
            queryParams.Add($"toUtc={Uri.EscapeDataString(searchRequest.ToUtc.Value.ToString("o"))}");
        }

        if (!string.IsNullOrWhiteSpace(searchRequest.InvoiceNumber))
        {
            queryParams.Add($"invoiceNumber={Uri.EscapeDataString(searchRequest.InvoiceNumber.Trim())}");
        }

        if (searchRequest.CashierId.HasValue)
        {
            queryParams.Add($"cashierId={searchRequest.CashierId.Value}");
        }

        if (searchRequest.TerminalId.HasValue)
        {
            queryParams.Add($"terminalId={searchRequest.TerminalId.Value}");
        }

        var url = $"{_httpsBaseUrl}/api/v1/sales?{string.Join("&", queryParams)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<PagedResult<SaleSummaryDto>>(response, ct);
    }

    public async Task<ApiResponse<SaleInvoiceDto>> GetSaleByIdAsync(
        Guid id,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/sales/{id}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<SaleInvoiceDto>(response, ct);
    }

    public async Task<ApiResponse<SaleInvoiceDto>> GetSaleByInvoiceNumberAsync(
        string invoiceNumber,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);
        var url = $"{_httpsBaseUrl}/api/v1/sales/invoice/{Uri.EscapeDataString(invoiceNumber.Trim())}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<SaleInvoiceDto>(response, ct);
    }

    public async Task<ApiResponse<ReceiptPrintResponseDto>> ReprintReceiptAsync(
        Guid saleInvoiceId,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/sales/{saleInvoiceId}/reprint";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        ApplySecurityHeaders(request, token, attachTerminalHeaders: true);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<ReceiptPrintResponseDto>(response, ct);
    }

    public async Task<ApiResponse<IReadOnlyList<SaleRefundDto>>> GetSaleRefundsAsync(
        Guid saleInvoiceId,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/sales/{saleInvoiceId}/refunds";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<IReadOnlyList<SaleRefundDto>>(response, ct);
    }

    public async Task<ApiResponse<StoreSettingsDto>> GetStoreSettingsAsync(
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/store/settings";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<StoreSettingsDto>(response, ct);
    }

    public async Task<ApiResponse<PagedResult<CustomerDto>>> SearchCustomersAsync(
        string? searchTerm,
        bool? isActive,
        int page = 1,
        int pageSize = 20,
        string? token = null,
        CancellationToken ct = default)
    {
        var queryParams = new List<string>
        {
            $"page={Math.Max(1, page)}",
            $"pageSize={Math.Clamp(pageSize, 1, 100)}"
        };

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            queryParams.Add($"searchTerm={Uri.EscapeDataString(searchTerm.Trim())}");
        }

        if (isActive.HasValue)
        {
            queryParams.Add($"isActive={isActive.Value.ToString().ToLowerInvariant()}");
        }

        var url = $"{_httpsBaseUrl}/api/v1/customers?{string.Join("&", queryParams)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<PagedResult<CustomerDto>>(response, ct);
    }

    public async Task<ApiResponse<CustomerDto>> GetCustomerByIdAsync(
        Guid id,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/customers/{id}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CustomerDto>(response, ct);
    }

    public async Task<ApiResponse<CustomerDto>> GetCustomerByPhoneAsync(
        string phone,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        var url = $"{_httpsBaseUrl}/api/v1/customers/by-phone/{Uri.EscapeDataString(phone.Trim())}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CustomerDto>(response, ct);
    }

    public async Task<ApiResponse<CustomerDto>> CreateCustomerAsync(
        CreateCustomerRequest createRequest,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(createRequest);
        var url = $"{_httpsBaseUrl}/api/v1/customers";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(createRequest)
        };
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CustomerDto>(response, ct);
    }

    public async Task<ApiResponse<CustomerDto>> UpdateCustomerAsync(
        Guid id,
        UpdateCustomerRequest updateRequest,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(updateRequest);
        var url = $"{_httpsBaseUrl}/api/v1/customers/{id}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(updateRequest)
        };
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CustomerDto>(response, ct);
    }

    public async Task<ApiResponse<CustomerDto>> UpdateCreditLimitAsync(
        Guid id,
        UpdateCreditLimitRequest updateLimitRequest,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(updateLimitRequest);
        var url = $"{_httpsBaseUrl}/api/v1/customers/{id}/credit-limit";
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(updateLimitRequest)
        };
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CustomerDto>(response, ct);
    }

    public async Task<ApiResponse<CustomerDto>> SetCustomerStatusAsync(
        Guid id,
        SetCustomerStatusRequest statusRequest,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statusRequest);
        var url = $"{_httpsBaseUrl}/api/v1/customers/{id}/status";
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(statusRequest)
        };
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CustomerDto>(response, ct);
    }

    public async Task<ApiResponse<CustomerPaymentDto>> RecordCustomerPaymentAsync(
        Guid customerId,
        RecordCustomerPaymentRequest paymentRequest,
        Guid? terminalId = null,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paymentRequest);
        var url = $"{_httpsBaseUrl}/api/v1/customers/{customerId}/payments";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(paymentRequest)
        };
        ApplySecurityHeaders(request, token, attachTerminalHeaders: true, terminalId: terminalId);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CustomerPaymentDto>(response, ct);
    }

    public async Task<ApiResponse<IReadOnlyList<CustomerPaymentDto>>> GetCustomerPaymentsAsync(
        Guid customerId,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/customers/{customerId}/payments";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<IReadOnlyList<CustomerPaymentDto>>(response, ct);
    }

    public async Task<ApiResponse<IReadOnlyList<CustomerLedgerEntryDto>>> GetCustomerLedgerAsync(
        Guid customerId,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/customers/{customerId}/ledger";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<IReadOnlyList<CustomerLedgerEntryDto>>(response, ct);
    }

    public async Task<ApiResponse<IReadOnlyList<SaleInvoiceDto>>> GetCustomerSalesAsync(
        Guid customerId,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/customers/{customerId}/sales";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<IReadOnlyList<SaleInvoiceDto>>(response, ct);
    }

    public async Task<ApiResponse<DailySalesSummaryReportDto>> GetDailySalesSummaryAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? terminalId = null,
        string? token = null,
        CancellationToken ct = default)
    {
        var queryParams = new List<string>();
        if (fromDate.HasValue)
            queryParams.Add($"fromDate={Uri.EscapeDataString(fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
        if (toDate.HasValue)
            queryParams.Add($"toDate={Uri.EscapeDataString(toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
        if (terminalId.HasValue && terminalId.Value != Guid.Empty)
            queryParams.Add($"terminalId={terminalId.Value}");

        var url = $"{_httpsBaseUrl}/api/v1/reports/daily-sales{(queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "")}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<DailySalesSummaryReportDto>(response, ct);
    }

    public async Task<ApiResponse<TaxSummaryReportDto>> GetTaxSummaryAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? token = null,
        CancellationToken ct = default)
    {
        var queryParams = new List<string>();
        if (fromDate.HasValue)
            queryParams.Add($"fromDate={Uri.EscapeDataString(fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
        if (toDate.HasValue)
            queryParams.Add($"toDate={Uri.EscapeDataString(toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");

        var url = $"{_httpsBaseUrl}/api/v1/reports/tax-summary{(queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "")}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<TaxSummaryReportDto>(response, ct);
    }

    public async Task<ApiResponse<ProductPerformanceReportDto>> GetProductPerformanceAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int? categoryId = null,
        Guid? productId = null,
        int page = 1,
        int pageSize = 50,
        string? token = null,
        CancellationToken ct = default)
    {
        var queryParams = new List<string>
        {
            $"page={Math.Max(1, page)}",
            $"pageSize={Math.Clamp(pageSize, 1, 200)}"
        };
        if (fromDate.HasValue)
            queryParams.Add($"fromDate={Uri.EscapeDataString(fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
        if (toDate.HasValue)
            queryParams.Add($"toDate={Uri.EscapeDataString(toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
        if (categoryId.HasValue)
            queryParams.Add($"categoryId={categoryId.Value}");
        if (productId.HasValue && productId.Value != Guid.Empty)
            queryParams.Add($"productId={productId.Value}");

        var url = $"{_httpsBaseUrl}/api/v1/reports/product-sales?{string.Join("&", queryParams)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<ProductPerformanceReportDto>(response, ct);
    }

    public async Task<ApiResponse<CashierPerformanceReportDto>> GetCashierPerformanceAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? cashierUserId = null,
        string? token = null,
        CancellationToken ct = default)
    {
        var queryParams = new List<string>();
        if (fromDate.HasValue)
            queryParams.Add($"fromDate={Uri.EscapeDataString(fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
        if (toDate.HasValue)
            queryParams.Add($"toDate={Uri.EscapeDataString(toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
        if (cashierUserId.HasValue && cashierUserId.Value != Guid.Empty)
            queryParams.Add($"cashierUserId={cashierUserId.Value}");

        var url = $"{_httpsBaseUrl}/api/v1/reports/cashier-summary{(queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "")}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CashierPerformanceReportDto>(response, ct);
    }

    public async Task<ApiResponse<CurrentTerminalReportDto>> GetCurrentTerminalReportAsync(
        Guid? terminalId = null,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = terminalId.HasValue && terminalId.Value != Guid.Empty
            ? $"{_httpsBaseUrl}/api/v1/reports/current-terminal?terminalId={terminalId.Value}"
            : $"{_httpsBaseUrl}/api/v1/reports/current-terminal";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CurrentTerminalReportDto>(response, ct);
    }

    public async Task<ApiResponse<CashSessionDto>> GetActiveCashSessionAsync(
        Guid terminalId,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/cash-sessions/current?terminalId={terminalId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CashSessionDto>(response, ct);
    }

    public async Task<ApiResponse<CashSessionReportDto>> GetXReportAsync(
        Guid sessionId,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/cash-sessions/{sessionId}/x-report";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CashSessionReportDto>(response, ct);
    }

    public async Task<ApiResponse<CashSessionReportDto>> GetZReportAsync(
        Guid sessionId,
        string? token = null,
        CancellationToken ct = default)
    {
        var url = $"{_httpsBaseUrl}/api/v1/cash-sessions/{sessionId}/z-report";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadResponseAsync<CashSessionReportDto>(response, ct);
    }

    public async Task<ApiResponse<bool>> DownloadReportCsvAsync(
        string reportType,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? terminalId = null,
        Guid? cashierUserId = null,
        int? categoryId = null,
        Stream? destinationStream = null,
        string? token = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportType);
        ArgumentNullException.ThrowIfNull(destinationStream);

        var queryParams = new List<string>();
        if (fromDate.HasValue)
            queryParams.Add($"fromDate={Uri.EscapeDataString(fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
        if (toDate.HasValue)
            queryParams.Add($"toDate={Uri.EscapeDataString(toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
        if (terminalId.HasValue && terminalId.Value != Guid.Empty)
            queryParams.Add($"terminalId={terminalId.Value}");
        if (cashierUserId.HasValue && cashierUserId.Value != Guid.Empty)
            queryParams.Add($"cashierUserId={cashierUserId.Value}");
        if (categoryId.HasValue)
            queryParams.Add($"categoryId={categoryId.Value}");

        var queryString = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : string.Empty;
        var url = $"{_httpsBaseUrl}/api/v1/reports/{Uri.EscapeDataString(reportType.Trim().ToLowerInvariant())}/csv{queryString}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityHeaders(request, token);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return await ReadResponseAsync<bool>(response, ct);
        }

        var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using (contentStream.ConfigureAwait(false))
        {
            await contentStream.CopyToAsync(destinationStream, ct);
        }

        return ApiResponse.Ok(true);
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
            // fallback if body cannot be parsed or is empty
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return ApiResponse.Fail<T>("Authentication required. Please log in again.", "UNAUTHORIZED");
        }
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return ApiResponse.Fail<T>("Permission denied for this operation.", "FORBIDDEN");
        }
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return ApiResponse.Fail<T>("Requested resource not found.", "NOT_FOUND");
        }
        if (!response.IsSuccessStatusCode)
        {
            return ApiResponse.Fail<T>($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}", response.StatusCode.ToString());
        }

        return ApiResponse.Fail<T>("Empty response received from store server.", "EMPTY_RESPONSE");
    }
}
