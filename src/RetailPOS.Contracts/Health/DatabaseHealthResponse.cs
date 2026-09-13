namespace RetailPOS.Contracts.Health;

/// <summary>
/// Status and integrity metrics for the authoritative store SQLite database.
/// </summary>
public sealed record DatabaseHealthResponse(
    string Status,
    bool IsConnected,
    string IntegrityStatus,
    bool ForeignKeysValid,
    string Message,
    DateTime TimestampUtc);
