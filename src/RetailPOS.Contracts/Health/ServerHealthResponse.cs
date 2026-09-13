namespace RetailPOS.Contracts.Health;

/// <summary>
/// Authoritative server liveness and identification status.
/// </summary>
public sealed record ServerHealthResponse(
    string Status,
    string Version,
    string Runtime,
    string ServerId,
    string StoreId,
    string StoreName,
    DateTime TimestampUtc);
