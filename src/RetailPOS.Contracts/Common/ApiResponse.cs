namespace RetailPOS.Contracts.Common;

/// <summary>
/// Standard unified API response envelope for all /api/v1 endpoints.
/// </summary>
public sealed record ApiResponse<T>(
    bool Success,
    T? Data,
    string? ErrorMessage,
    string? ErrorCode,
    DateTime TimestampUtc);

/// <summary>
/// Static factory methods for creating ApiResponse instances (adheres to CA1000).
/// </summary>
public static class ApiResponse
{
    public static ApiResponse<T> Ok<T>(T data) =>
        new(true, data, null, null, DateTime.UtcNow);

    public static ApiResponse<T> Fail<T>(string errorMessage, string? errorCode = null) =>
        new(false, default, errorMessage, errorCode, DateTime.UtcNow);
}
