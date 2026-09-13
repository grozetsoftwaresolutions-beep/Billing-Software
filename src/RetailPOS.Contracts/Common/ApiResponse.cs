namespace RetailPOS.Contracts.Common;

/// <summary>
/// Standard unified API response envelope for all /api/v1 endpoints.
/// Strictly encapsulates data, user-safe error messages, error codes, and correlation IDs.
/// </summary>
public sealed record ApiResponse<T>(
    bool Success,
    T? Data,
    string? ErrorMessage,
    string? ErrorCode,
    string? CorrelationId,
    DateTime TimestampUtc);

/// <summary>
/// Static factory helper for creating ApiResponse instances.
/// </summary>
public static class ApiResponse
{
    public static ApiResponse<T> Ok<T>(T data, string? correlationId = null) =>
        new(true, data, null, null, correlationId, DateTime.UtcNow);

    public static ApiResponse<T> Fail<T>(string errorMessage, string? errorCode = null, string? correlationId = null) =>
        new(false, default, errorMessage, errorCode, correlationId, DateTime.UtcNow);
}
